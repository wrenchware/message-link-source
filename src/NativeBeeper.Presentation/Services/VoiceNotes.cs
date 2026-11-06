using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

// Recording a voice message in the composer. The microphone itself is behind IVoiceRecorder, so everything around it —
// the time limit, the temporary file and when it is deleted, what is said when Windows refuses the microphone, which
// services are offered it — is free of Windows types and checked offline with a fake recorder.
//
// Nothing here sends. A finished recording is only handed back as bytes, which the composer stages beside its text as
// it stages any file; it goes when Send is pressed, through the same attachment path, and not otherwise.

public enum VoiceRecorderStart { Started, AccessDenied, NoMicrophone, Failed }

public interface IVoiceRecorder : IDisposable
{
    // Starts capturing the default microphone into an AAC (.m4a) file at path, which exists and is empty.
    Task<VoiceRecorderStart> StartAsync(string path, CancellationToken cancellationToken = default);
    // The loudest sample, 0 to 1, since this was last read. Read on the interface thread; written from the audio one.
    double TakeLevel();
    // Stops and finishes the file. False when it could not be finished; the file is then worth nothing.
    Task<bool> StopAsync();
    // Set once the microphone has gone away or the capture broke mid-way. Read on each tick.
    bool HasFailed { get; }
    // Dispose stops without finishing the file, synchronously: it is what a closing window and Cancel both call.
}

public enum VoiceNoteState { Idle, Starting, Recording, Finishing }
public enum VoiceNoteTick { Idle, Recording, LimitReached, Failed }

public sealed record VoiceNote(byte[] Bytes, TimeSpan Duration, string Name, string MimeType);

public static class VoiceNoteRules
{
    // AAC in an MP4 container is what every service here takes as audio, and what upstream's bridges turn a voice
    // message into before sending it: Google Messages and both Meta services list audio/mp4 as fully supported.
    public const string FileName = "Voice message.m4a", MimeType = "audio/mp4";
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    // Shorter than this is a slip of the finger, not a message. Discarded with a word rather than staged.
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1);
    // Five minutes of mono AAC at 96 kbps is about 3.6 MB. Anything far past that is not what was asked for.
    public const long ByteLimit = 16L * 1024 * 1024;

    public const string AccessDenied = "Windows is blocking Messages from using the microphone. To record, turn on " +
        "Microphone access and Let desktop apps access your microphone in Settings > Privacy & security > Microphone.";
    public const string NoMicrophone = "No microphone was found. Connect one and try again.";
    public const string CouldNotStart = "Messages couldn't start recording. Check the microphone and try again.";
    public const string DeviceLost = "Recording stopped because the microphone stopped working. Nothing was kept.";
    public const string CouldNotFinish = "Messages couldn't finish the recording. Nothing was kept.";
    public const string TooShort = "That recording was too short to send.";
    public const string LimitReached = "Recordings stop at 5 minutes. This one is ready to review and send.";
    public const string Busy = "Another conversation is recording. Stop that recording first.";
    public const string SomethingStaged = "Send or remove the attachment that's waiting before recording.";
    public const string Reply = "Cancel the selected reply before recording. Replies can be text only.";
    // While recording, nothing else is staged: Stop has to find the composer as recording left it.
    public const string Recording = "Stop or cancel the recording before adding anything else.";
    // A recording ended by something other than Cancel says so, since nothing else on screen would.
    public const string RouteChanged = "The recording was discarded because this conversation's sending service changed.";
    public const string LeftConversation = "The recording was discarded because the conversation was closed or changed.";
    public const string ConnectionChanged = "The recording was discarded because the connection changed while recording.";
    // A finished recording the composer could not take just then is kept in memory, to be added with Add recording.
    public const string KeptWhileBusy = "Another attachment was still being prepared. Your recording is kept: choose Add recording.";
    public const string KeptBesideAttachment = "A recording goes on its own. Send or remove the attachment that's waiting, then choose Add recording.";
    public const string KeptDraftLimit = "Send or remove an attachment waiting in another conversation, then choose Add recording.";
    public const string KeptFailed = "The recording couldn't be added. It's kept: choose Add recording to try again.";
    public const string Waiting = "A recording is waiting. Choose Add recording to put it in the composer, where it can be sent or deleted.";
    public const string HeldDiscarded = "The recording that was waiting was discarded because the conversation changed.";

    /// <summary>
    /// Why a finished recording cannot be put in the composer now, and whether it is kept to try again. A conversation that
    /// changed loses it, since it would otherwise go to someone else; anything that only has to clear first keeps it.
    /// Null when nothing stands in the way.
    /// </summary>
    public static (string Text, bool Keep)? StageRefusal(bool conversationChanged, bool busy, bool attachmentWaiting, bool draftLimit) =>
        conversationChanged ? (LeftConversation, false)
        : busy ? (KeptWhileBusy, true)
        : attachmentWaiting ? (KeptBesideAttachment, true)
        : draftLimit ? (KeptDraftLimit, true)
        : null;

    /// <summary>
    /// Whether a change of send route ends a recording in progress. A recording belongs to the conversation it was started
    /// in; a route that is briefly none (the merged conversation being read again) has not gone anywhere else.
    /// </summary>
    public static bool RouteEndsRecording(string? recordingChat, string? route) => route is not null && route != recordingChat;

    // Which services are offered the microphone: those whose attachment path in this app already sends an audio file, and
    // where a voice message has somewhere it can be tried first. Google Messages takes audio/* (GoogleMediaBackend) and
    // Messenger any file its upload accepts (MessengerBackend), and both have approved test conversations. Instagram's
    // upload takes one too and its helper knows the voice flag, but a voice message there has never been sent, and the
    // account is the user's real one with no approved conversation to try it in; unknown is unavailable, so it is not
    // offered until the user has sent one and confirmed it. Google Voice sends photos only (GoogleVoicePhotos), and
    // nothing this list does not name is offered either.
    public static bool Offered(string? network) => network is "Google Messages" or "Messenger";

    // What Ctrl+Shift+R says where the microphone is not offered, so the shortcut is not a dead key. Google Voice's send
    // call carries a picture and nothing else: its media types are JPEG, PNG, TIFF, GIF, BMP and WebP, and upstream's bridge
    // declares images only. An unknown service says nothing, as before.
    public const string NotOnGoogleVoice = "Google Voice can't send voice messages. It sends text and photos only.";
    public const string NotOnInstagram = "Recording voice messages isn't available for Instagram here.";
    public static string? NotOffered(string? network) => network switch
    {
        "Google Voice" => NotOnGoogleVoice,
        "Instagram" => NotOnInstagram,
        _ => null,
    };

    public static string Describe(VoiceRecorderStart result) => result switch
    {
        VoiceRecorderStart.AccessDenied => AccessDenied,
        VoiceRecorderStart.NoMicrophone => NoMicrophone,
        VoiceRecorderStart.Started => "",
        _ => CouldNotStart,
    };

    // The file the provider is handed, read from the bytes kept in memory, and marked as a voice message: Messenger and
    // Instagram then send it as one (a voice clip, drawn with its own player) rather than as an audio file. Its length goes
    // with it, so an encrypted Messenger conversation draws the message with its duration before it is played.
    public static AttachmentFile File(byte[] bytes, TimeSpan duration) => new(FileName, MimeType, bytes.LongLength,
        ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(new MemoryStream(bytes, false)); }, IsVoiceNote: true,
        DurationSeconds: duration > TimeSpan.Zero ? duration.TotalSeconds : null);

    // A recording goes alone: never beside a photo or file already waiting, which the attachment rules would refuse
    // anyway, and never as a reply, which no attachment here can be.
    public static string? StartProblem(int stagedCount, bool hasReply) =>
        hasReply ? Reply : stagedCount > 0 ? SomethingStaged : null;

    // Text written beside a recording is sent as its own message after it. Messenger would otherwise carry it as the
    // recording's caption, which an encrypted Messenger audio message has nowhere to put.
    public static bool UseCaption(bool isRecording, AttachmentCaptionSupport support) => !isRecording && support.Supported;

    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    // The temporary files recordings are written to while they run. Anything left in the folder is from a run that
    // ended mid-recording, and is deleted at the next start. Only these names, never anything else in the folder.
    public const string FilePrefix = "voice-", FileExtension = ".m4a";
    public static string NewPath(string directory) => Path.Combine(directory, FilePrefix + Guid.NewGuid().ToString("N") + FileExtension);

    public static int Sweep(string directory)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            foreach (var file in Directory.EnumerateFiles(directory, FilePrefix + "*" + FileExtension))
                if (TryDelete(file)) removed++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return false;
            System.IO.File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

// The level meter's bars: the newest level at the right, the older ones sliding left. Levels arrive as the loudest
// sample of each tick and are drawn on a decibel scale, since speech lives far below full scale and a linear bar would
// barely move; the fall is eased so a pause between words does not blink the bar to nothing.
public sealed class VoiceNoteMeter(int count)
{
    private const double Floor = -50;
    private readonly double[] levels = new double[Math.Max(1, count)];
    private double last;
    public IReadOnlyList<double> Levels => levels;

    public void Push(double peak)
    {
        var shown = Scale(peak);
        // Rises at once; falls by at most a third of the bar per tick.
        shown = Math.Max(shown, last - 0.34);
        last = shown;
        Array.Copy(levels, 1, levels, 0, levels.Length - 1);
        levels[^1] = shown;
    }

    public void Clear() { Array.Clear(levels); last = 0; }

    public static double Scale(double peak)
    {
        if (!double.IsFinite(peak) || peak <= 0) return 0;
        var db = 20 * Math.Log10(Math.Min(1, peak));
        return Math.Clamp((db - Floor) / -Floor, 0, 1);
    }
}

// One recording, from Start to the bytes it made or to nothing. The temporary file lives only while the recording runs:
// Stop reads it into memory and deletes it, and Cancel, a failure, a refused microphone and Dispose delete it at once.
// Deleting is synchronous, so a window closing (or Windows ending the session) leaves no file behind and starts no work
// it cannot finish; a file Windows would not let go of is left to the sweep at the next start.
public sealed class VoiceNoteSession(IVoiceRecorder recorder, string directory, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long startedAt;
    private bool disposed;
    public string FilePath { get; } = VoiceNoteRules.NewPath(directory);
    public VoiceNoteState State { get; private set; }
    // Why the last step came to nothing, in words for the conversation's notice; empty when there is nothing to say.
    public string Problem { get; private set; } = "";
    // What happened, for app.log: a fixed word, never anything recorded.
    public string Outcome { get; private set; } = "none";

    public TimeSpan Elapsed => State is VoiceNoteState.Recording or VoiceNoteState.Finishing
        ? Min(time.GetElapsedTime(startedAt), VoiceNoteRules.Limit) : TimeSpan.Zero;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || State != VoiceNoteState.Idle) return false;
        State = VoiceNoteState.Starting;
        VoiceRecorderStart result;
        try
        {
            Directory.CreateDirectory(directory);
            // Created empty first: the recorder is handed a file that exists, as the Windows file APIs want.
            using (new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            result = await recorder.StartAsync(FilePath, cancellationToken);
        }
        catch (UnauthorizedAccessException) { result = VoiceRecorderStart.AccessDenied; }
        catch (OperationCanceledException) { result = VoiceRecorderStart.Failed; }
        catch (Exception) { result = VoiceRecorderStart.Failed; }
        // Cancelled, or the window closed, while the microphone was being opened: Dispose has cleaned up already, and
        // does so again here for anything the start made after it.
        if (disposed) { Discard(); Outcome = "cancelled"; return false; }
        if (result != VoiceRecorderStart.Started)
        {
            Problem = VoiceNoteRules.Describe(result);
            Outcome = result switch { VoiceRecorderStart.AccessDenied => "denied", VoiceRecorderStart.NoMicrophone => "no_microphone", _ => "start_failed" };
            Discard();
            State = VoiceNoteState.Idle;
            return false;
        }
        startedAt = time.GetTimestamp();
        State = VoiceNoteState.Recording;
        Outcome = "recording";
        return true;
    }

    // Called on each tick of the recording bar: whether it is time to stop on its own.
    public VoiceNoteTick Tick()
    {
        if (State != VoiceNoteState.Recording) return State == VoiceNoteState.Idle ? VoiceNoteTick.Idle : VoiceNoteTick.Recording;
        if (recorder.HasFailed) return VoiceNoteTick.Failed;
        return time.GetElapsedTime(startedAt) >= VoiceNoteRules.Limit ? VoiceNoteTick.LimitReached : VoiceNoteTick.Recording;
    }

    public double TakeLevel() => State == VoiceNoteState.Recording ? recorder.TakeLevel() : 0;

    // Stops and hands back what was recorded, or null with Problem saying why there is nothing. The file is gone
    // either way when this returns.
    public async Task<VoiceNote?> StopAsync()
    {
        if (disposed || State != VoiceNoteState.Recording) return null;
        var duration = Elapsed;
        State = VoiceNoteState.Finishing;
        var failed = recorder.HasFailed;
        bool finished;
        try { finished = !failed && await recorder.StopAsync(); }
        catch (Exception) { finished = false; }
        byte[]? bytes = null;
        if (finished && !disposed)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length is > 0 and <= VoiceNoteRules.ByteLimit) bytes = await System.IO.File.ReadAllBytesAsync(FilePath);
                else if (info.Exists && info.Length > VoiceNoteRules.ByteLimit) Outcome = "too_large";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bytes = null; }
        }
        var closedMeanwhile = disposed;
        Discard();
        State = VoiceNoteState.Idle;
        if (closedMeanwhile) { Outcome = "cancelled"; return null; }
        if (failed) { Problem = VoiceNoteRules.DeviceLost; Outcome = "device_lost"; return null; }
        if (duration < VoiceNoteRules.Shortest) { Problem = VoiceNoteRules.TooShort; Outcome = "too_short"; return null; }
        if (bytes is not { Length: > 0 })
        {
            Problem = VoiceNoteRules.CouldNotFinish;
            if (Outcome != "too_large") Outcome = "finish_failed";
            return null;
        }
        Outcome = "recorded";
        return new VoiceNote(bytes, duration, VoiceNoteRules.FileName, VoiceNoteRules.MimeType);
    }

    // Stops without keeping anything. Synchronous and safe to call at any point, twice, or while starting or finishing.
    public void Cancel()
    {
        if (disposed) return;
        disposed = true;
        if (Outcome is "none" or "recording") Outcome = "cancelled";
        Discard();
        State = VoiceNoteState.Idle;
    }

    public void Dispose() => Cancel();

    private void Discard()
    {
        try { recorder.Dispose(); } catch (Exception) { }
        VoiceNoteRules.TryDelete(FilePath);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
