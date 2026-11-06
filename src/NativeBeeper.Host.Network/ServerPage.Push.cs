using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// ---- Notifications on phones (SWITCHBOARD.md 7.5, 10.9): the owner's Firebase project given here, as a wizard, with a test push that proves it end to end ----
internal sealed partial class ServerPage
{
    private const string FirebaseConsole = "https://console.firebase.google.com/";

    // The Android app a project needs, and what to call the project: any name works, but people ask what to type.
    private const string MessagesApp = "com.wrenchware.messages", ProjectName = "Messages Notifications";

    // A Firebase project's own page (settings/general, settings/serviceaccounts/adminsdk), or the console's front page while no project is known.
    private static string FirebasePage(string? project, string page) =>
        project is null ? FirebaseConsole : FirebaseConsole + "project/" + Uri.EscapeDataString(project) + "/" + page;

    private const string GeneralPage = "settings/general", KeysPage = "settings/serviceaccounts/adminsdk";

    // A project id as Google makes them, before it goes into a link; one read from files the server already kept is checked here too.
    private static string? ProjectOf(string? id) =>
        id is { Length: >= 4 and <= 64 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ? id : null;

    // The card: off, the way to set them up; on, each phone that matters with its test, then the project and when one last went.
    private void PushCard(StringBuilder body, Pushes card, string token)
    {
        body.Append("<section id=\"push\"><h2>Notifications on phones</h2>");
        if (card.Switch.Current is not { } setup)
        {
            body.Append(Para("New messages on a phone go through a Firebase project of your own, and each is sealed for your phone: Google carries it without being able to read it."))
                .Append("<form method=\"get\" action=\"/push\"><button type=\"submit\">Set up notifications…</button></form></section>");
            return;
        }
        var project = ProjectOf(setup.Offer.ProjectId);
        if (card.Health.Test is { } test) body.Append("<p class=\"notice\" role=\"status\">").Append(Html(TestWords(test))).Append("</p>");
        body.Append("<div class=\"sh\"><h2>Phones</h2></div><div class=\"box\">");
        var any = false;
        var tests = new StringBuilder();
        // One device at a time, so each line knows the device its test goes to: two phones can share a name.
        foreach (var device in devices.List(null).OrderBy(device => device.Name, StringComparer.CurrentCulture))
        {
            if (card.Health.Status(true, card.Registry, [device]).Phones is not [var phone]) continue;
            any = true;
            if (card.Registry.Get(device.Id) is not null && card.Registry.Paired(device.Id))
                tests.Append("<form method=\"post\" action=\"/push/test\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\"><input type=\"hidden\" name=\"id\" value=\"").Append(Html(device.Id))
                    .Append("\"><button type=\"submit\" class=\"quiet\">").Append(Html("Send a test to " + phone.Name)).Append("</button></form>");
            body.Append(phone.State switch
            {
                PushPhoneStates.On => Row(phone.Name, null, "Signed up", null),
                PushPhoneStates.Failing => Row(phone.Name, $"{phone.Failures} failed: {PushStatus.Reason(phone)}", "Failing", null, warn: true),
                PushPhoneStates.MissingApp => Line(false, phone.Name, $"Add {phone.Package} in Firebase, download google-services.json again and replace the files.", Link(FirebasePage(project, GeneralPage), "Open Project settings"), "No app in Firebase"),
                _ => Row(phone.Name, "Open Message Link on it; its Settings say what it needs.", "Not signed up", null, warn: true),
            });
        }
        if (!any) body.Append(Row("No phone yet", "Open Message Link on your phone once: it signs up by itself when it connects.", null, null));
        body.Append("</div>");
        if (tests.Length > 0) body.Append("<div class=\"btns\">").Append(tests).Append("</div>");
        body.Append("<div class=\"sh\"><h2>Firebase</h2></div><div class=\"box\">")
            .Append(Row("Project", null, setup.Offer.ProjectId ?? "", null))
            .Append(Row("Last sent", null, card.Health.LastSent is { } sent ? Ago(sent) : "Nothing yet", null))
            .Append("</div><div class=\"btns\"><a class=\"button quiet\" href=\"/push\">Replace the files…</a></div></section>");
    }

    // The test push as it stands, in the page's words.
    private static string TestWords(PushTest test) => test.State switch
    {
        PushTestState.Waiting => $"Test sent to {test.Name}. Waiting for it to show…",
        PushTestState.Shown => $"Arrived on {test.Name} in {Seconds(test.ShownAt!.Value - test.At)}.",
        PushTestState.TimedOut => $"{test.Name} didn't show the test within {(int)PushHealth.TestWait.TotalSeconds} s. Google took it, so check that the phone is online, that notifications for Message Link are allowed in its settings, and that battery saving isn't holding Message Link back.",
        PushTestState.Refused => $"Google refused the test to {test.Name} (status {test.Status}). Try adding the files again; if it keeps happening, make a new private key.",
        PushTestState.Gone => $"Google says {test.Name}'s address for notifications is gone. Open Message Link on it: it signs up again by itself.",
        PushTestState.NotSignedUp => $"{test.Name} hasn't signed up for notifications. Open Message Link on it once.",
        _ => $"The server couldn't reach Google to send the test to {test.Name}. Try again in a minute.",
    };

    private static string Seconds(TimeSpan span) => span < TimeSpan.FromSeconds(1) ? "under a second" : $"{Math.Round(span.TotalSeconds):0} s";

    // Sends the owner's test push to the phone chosen, then back to the card, which says how it went and reloads itself while it is waited on. The sign-in and
    // the form's token are enough: a test shows one notification on the owner's own phone and changes nothing.
    private async Task PushTestAsync(HttpContext context, IFormCollection form, Pushes card)
    {
        var id = form["id"].ToString();
        if (card.Switch.Current is null || devices.List(null).FirstOrDefault(device => device.Id == id) is not { } device)
        {
            Redirect(context.Response, "/");
            return;
        }
        await card.Health.SendTestAsync(device.Id, device.Name, card.Registry, card.Switch, context.RequestAborted).ConfigureAwait(false);
        Redirect(context.Response, "/manage/notifications");
    }

    // What was wrong with the files given, with the page that fixes it when there is one.
    private sealed record PushProblemWords(string Text, string? Href = null, string? LinkText = null);

    // The wizard (a GET), then the files with the password itself: whoever holds the key decides where every notification goes.
    private async Task PushAsync(HttpContext context, IFormCollection? form, string token, Pushes card)
    {
        var replacing = card.Switch.Current is not null;
        string Ask(PushProblemWords? problem, string? project) => PushWizard(token, replacing, problem, project ?? ProjectOf(card.Switch.Current?.Offer.ProjectId));
        const string title = "Notifications";
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null, null)).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "push", title, problem => Ask(new(problem), null)).ConfigureAwait(false)) return;

        // Whichever files were given, in any order and under any field's name: each is told by what is in it.
        string? settings = null, account = null, project = null;
        var (unknown, tooLarge, settingsTwice, accountTwice) = (0, false, false, false);
        foreach (var file in form.Files.Take(4))
        {
            if (file.Length is 0 or > PushSwitch.MaxFile) { tooLarge |= file.Length > 0; continue; }
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            var text = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            var (kind, named) = FirebaseTransport.Classify(text);
            switch (kind)
            {
                case FirebaseFileKind.AppSettings:
                    settingsTwice |= settings is not null;
                    settings ??= text;
                    project = named ?? project;
                    break;
                case FirebaseFileKind.ServiceAccount:
                    accountTwice |= account is not null;
                    account ??= text;
                    project ??= named;
                    break;
                default:
                    unknown++;
                    break;
            }
        }
        if (settings is null || account is null)
        {
            var missing = settings is null && account is null
                ? new PushProblemWords(tooLarge ? "That file is too large to be one of Firebase's. Drop google-services.json and the private key."
                    : unknown > 0 ? "Those aren't Firebase's files. Drop google-services.json and the private key from your Firebase project."
                    : "Drop both files: google-services.json and the private key.")
                : settings is null
                    ? new PushProblemWords(accountTwice ? "Both files are private keys. The other one you need is google-services.json, in Project settings, under General."
                        : "That's the private key. Now add google-services.json too: it's in Project settings, under General, beside your Android apps.", FirebasePage(project, GeneralPage), "Open Project settings")
                    : new PushProblemWords(settingsTwice ? "Both files are google-services.json. The other one you need is the private key, from Project settings, under Service accounts."
                        : "That's google-services.json. Now add the private key too: in Project settings, under Service accounts, choose Generate new private key.", FirebasePage(project, KeysPage), "Open Service accounts");
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask(missing with { Text = missing.Text + " Choose both files at once." }, project)).ConfigureAwait(false);
            return;
        }
        var outcome = await card.Switch.InstallAsync(settings, account, context.RequestAborted).ConfigureAwait(false);
        PushProblemWords? said = outcome switch
        {
            PushInstall.NotAppSettings => new("google-services.json couldn't be read. Download it again from Project settings, under General.", FirebasePage(project, GeneralPage), "Open Project settings"),
            PushInstall.NotServiceAccount => new("The key file isn't a private key from Firebase. Make one in Project settings, under Service accounts, with Generate new private key.", FirebasePage(project, KeysPage), "Open Service accounts"),
            PushInstall.ProjectsDiffer => new("The two files are from different Firebase projects. Use both from the same project."),
            PushInstall.NoApp => new("google-services.json has no Android app for Message Link. Add com.wrenchware.messages, then download the file again.",
                FirebasePage(project, GeneralPage), "Open Project settings"),
            PushInstall.KeyRefused => new("Google didn't accept the private key: it may have been deleted. Make a new one in Service accounts and try again.", FirebasePage(project, KeysPage), "Open Service accounts"),
            PushInstall.ApiDisabled => new("Google says Cloud Messaging is turned off for this project. Turn on the Firebase Cloud Messaging API, wait a minute, then add the files again.",
                project is null ? "https://console.cloud.google.com/apis/library/fcm.googleapis.com" : "https://console.cloud.google.com/apis/library/fcm.googleapis.com?project=" + Uri.EscapeDataString(project),
                "Turn on the API"),
            PushInstall.NotAllowed => new("Google took the key but won't let it send notifications. Make a new private key in Service accounts (its account may send), or give the key's account the Firebase Cloud Messaging Admin role.",
                FirebasePage(project, KeysPage), "Open Service accounts"),
            PushInstall.NotSaved => new("The server couldn't keep the files, so nothing was changed. Try again."),
            _ => null,
        };
        if (said is not null)
        {
            await PageAsync(context, outcome == PushInstall.NotSaved ? StatusCodes.Status500InternalServerError : StatusCodes.Status400BadRequest, title, Ask(said, project)).ConfigureAwait(false);
            return;
        }
        Redirect(context.Response, outcome == PushInstall.On ? "/manage/notifications?done=push_on" : "/manage/notifications?done=push_on_unchecked");
    }

    // The wizard: the package names to copy, Firebase's pages (the project's own once a file or the server names it), the hard cases in plain words, and one drop
    // area for both files, whose few lines of script say which file is which and what is still missing before anything is sent.
    private static string PushWizard(string token, bool replacing, PushProblemWords? problem, string? project)
    {
        var body = new StringBuilder("<section id=\"wizard\">");
        if (problem is not null)
        {
            body.Append("<p class=\"notice\" role=\"status\">").Append(Html(problem.Text));
            if (problem.Href is { } href) body.Append(' ').Append(Link(href, problem.LinkText ?? "Open"));
            body.Append("</p>");
        }
        body.Append(Para(replacing
            ? "These replace the files the server has now, at once. With another project's files every phone signs up again by itself: phones with Message Link open at once, the others when Message Link next connects."
            : "Notifications start as soon as the two files are in; nothing needs restarting."));
        body.Append("<p>").Append(Link(JoinLink.FirebaseGuide, "See these steps with pictures")).Append("</p>");
        body.Append("<ol class=\"steps\">")
            .Append("<li><strong>Make a Firebase project.</strong> ").Append(Link(FirebaseConsole, "Open Firebase"))
            .Append("<span class=\"quiet block\">Sign in with a personal Google account, add a project named <strong>").Append(ProjectName).Append("</strong>, and accept Firebase's terms when it asks (only the first time). It's free, and Google Analytics isn't needed.</span></li>")
            .Append("<li><strong>Add the Android app.</strong><span class=\"quiet block\">In the project, add an Android app with this package name. You can skip the steps after it's registered.</span><ul class=\"packages\">")
            .Append("<li><span class=\"copy\">").Append(MessagesApp).Append("</span><button type=\"button\" class=\"quiet small\" data-copy=\"").Append(MessagesApp).Append("\">Copy</button></li>");
        body.Append("</ul></li>")
            .Append("<li><strong>Download google-services.json</strong> from ")
            .Append("<a data-firebase=\"").Append(GeneralPage).Append("\" href=\"").Append(Html(FirebasePage(project, GeneralPage))).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">Project settings, General</a>")
            .Append(", after adding the app, so it names it.</li>")
            .Append("<li><strong>Make a private key</strong> in ")
            .Append("<a data-firebase=\"").Append(KeysPage).Append("\" href=\"").Append(Html(FirebasePage(project, KeysPage))).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">Project settings, Service accounts</a>")
            .Append(" with <strong>Generate new private key</strong>. That file is a secret: give it only to this server.</li>")
            .Append("<li><strong>Drop both files below</strong>, in either order.</li></ol>");
        body.Append("<form method=\"post\" action=\"/push\" enctype=\"multipart/form-data\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
            .Append("<label class=\"drop\" id=\"drop\" for=\"files\"><strong>Drop google-services.json and the private key here</strong><span class=\"quiet\">or click to choose them</span>")
            .Append("<input id=\"files\" name=\"files\" type=\"file\" accept=\".json,application/json\" multiple></label>")
            .Append("<ul id=\"found\" class=\"found\" aria-live=\"polite\"></ul>")
            .Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
            .Append("<button type=\"submit\">").Append(replacing ? "Replace the files" : "Turn on notifications").Append("</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
        body.Append("<details><summary>Google says key creation is blocked</summary>")
            .Append(Para("Your Google account belongs to an organisation (Google Workspace, at work or school) whose policy forbids private key files. Make the Firebase project with a personal Google account instead, such as a gmail.com address, and start again from the first step."))
            .Append("</details>");
        body.Append("<script>").Append(PushScript).Append("</script>");
        return body.Append("</section>").ToString();
    }

    private const string PushStyle =
        ".quiet.block{display:block}ul.packages{list-style:none;margin:6px 0 0;padding:0}ul.packages li{display:flex;flex-wrap:wrap;align-items:center;gap:4px 10px;margin:0 0 6px}"
        + "button.small{font-size:13px;padding:2px 10px}label.drop{display:flex;flex-direction:column;align-items:center;gap:4px;text-align:center;border:2px dashed var(--line);border-radius:8px;"
        + "padding:20px 12px;margin:0 0 12px;cursor:pointer}label.drop.over{border-color:var(--accent);background:var(--attention)}label.drop input{max-width:100%;margin:6px 0 0}"
        + "ul.found{list-style:none;margin:0 0 12px;padding:0}ul.found li{padding:2px 0 2px 16px;position:relative}ul.found li:before{content:'';position:absolute;left:0;top:.6em;width:8px;height:8px;border-radius:50%;background:var(--wait)}"
        + "ul.found li.ok:before{background:var(--ok)}details{margin:12px 0 0}summary{cursor:pointer;color:var(--accent)}form.test{display:flex;flex-wrap:wrap;align-items:center;gap:8px}"
        + "form.test label{margin:0}form.test select{width:auto;margin:0}";

    /// <summary>
    /// The page's one script: copy buttons, the drop area, and reading the dropped files in the browser to say which is which, what is missing, and to point the links
    /// at the project they name. Nothing is sent anywhere but by the form itself. Allowed by its hash (<see cref="PushScriptHash"/>), so no other script can run.
    /// </summary>
    internal const string PushScript = """
(function(){
var C='https://console.firebase.google.com/',P=['com.wrenchware.messages'];
document.querySelectorAll('button[data-copy]').forEach(function(b){b.addEventListener('click',function(){var w=b.textContent;var t=b.getAttribute('data-copy');
if(navigator.clipboard)navigator.clipboard.writeText(t).then(function(){b.textContent='Copied';setTimeout(function(){b.textContent=w},1500)},function(){});});});
var input=document.getElementById('files'),drop=document.getElementById('drop'),found=document.getElementById('found');
if(!input||!drop||!found)return;
['dragenter','dragover'].forEach(function(e){drop.addEventListener(e,function(v){v.preventDefault();drop.classList.add('over')})});
['dragleave','drop'].forEach(function(e){drop.addEventListener(e,function(){drop.classList.remove('over')})});
drop.addEventListener('drop',function(v){v.preventDefault();if(v.dataTransfer&&v.dataTransfer.files.length){input.files=v.dataTransfer.files;show()}});
input.addEventListener('change',show);
function kind(j,n){if(j&&j.project_info&&Array.isArray(j.client))return{k:'s',n:n,p:j.project_info.project_id,apps:j.client.map(function(c){try{return c.client_info.android_client_info.package_name}catch(e){return ''}})};
if(j&&(j.type==='service_account'||j.private_key))return{k:'a',n:n,p:j.project_id};return{k:'u',n:n}}
function say(t,ok){var li=document.createElement('li');li.textContent=t;if(ok)li.className='ok';found.appendChild(li)}
function point(p){if(!/^[a-z0-9-]{4,64}$/.test(p||''))return;document.querySelectorAll('a[data-firebase]').forEach(function(a){a.href=C+'project/'+p+'/'+a.getAttribute('data-firebase')})}
function show(){found.textContent='';var files=Array.prototype.slice.call(input.files||[]);
Promise.all(files.map(function(f){return f.size>30720?Promise.resolve({k:'big',n:f.name}):f.text().then(function(t){try{return kind(JSON.parse(t),f.name)}catch(e){return{k:'u',n:f.name}}})})).then(function(rs){
var s=rs.filter(function(r){return r.k==='s'})[0],a=rs.filter(function(r){return r.k==='a'})[0];
rs.forEach(function(r){if(r.k==='s')say(r.n+': google-services.json for '+r.p+'.',true);else if(r.k==='a')say(r.n+': the private key for '+r.p+'.',true);
else if(r.k==='big')say(r.n+' is too large to be one of Firebase\'s files.');else say(r.n+' isn\'t one of Firebase\'s files.')});
point((s||a||{}).p);
if(s){var none=P.every(function(p){return s.apps.indexOf(p)<0});
if(none)say('This project has no app for Messages. Add '+P[0]+', then download google-services.json again.')}
if(!s)say('Still needed: google-services.json, from Project settings, General.');if(!a)say('Still needed: the private key, from Project settings, Service accounts.');
if(s&&a)say(s.p===a.p?'Both files are here. Type the password and turn it on.':'The two files are from different projects. Use both from the same one.',s.p===a.p);});}
})();
""";

    /// <summary>The Content-Security-Policy source that lets <see cref="PushScript"/> run, and nothing else.</summary>
    internal static readonly string PushScriptHash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(PushScript)));
}
