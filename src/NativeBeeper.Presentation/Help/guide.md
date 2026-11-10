# The Message Link guide

Everything Message Link does, from the first sign-in to the last keyboard shortcut. Use the contents on the left, or search: every word in every chapter is searchable. Press <kbd>F1</kbd> anywhere in Message Link to come back here.

> **Tip:** New to Message Link? The two-minute tour points out what's easy to miss. Choose **Take the tour** under the contents, or **More options → Take the tour** in the main window.

## 1. Getting started {#basics}

**Message Link** is a native Windows 11 app that puts your texts and chats in one inbox. It connects directly to four services:

- **Google Messages**: your Android phone's texts, MMS and RCS chats.
- **Messenger**: your Facebook Messenger chats, including end-to-end encrypted ones.
- **Google Voice**: texts, call history and voicemail, for as many Voice numbers as you add.
- **Instagram**: your direct messages.

It is a real Windows app, not a web page wrapped in a window. A browser window appears only when a service asks you to sign in. It follows Windows' own settings: animations switch off with Windows' *Animation effects*, high contrast uses your system colours, and every button has a name for screen readers.

### The window at a glance {#window}

- **The conversation list** is on the left. Each row shows the person or group, a preview of the newest message and when it arrived. A small badge on the photo says which service it is from when you have more than one account connected.
- **The filters** sit above the list: a view (Messages, Unread, Archive, Spam, Drafts), the services, and your labels. See [Narrowing the list](#filters-and-views).
- **The conversation** is on the right, with the message box at the bottom.
- **The search box** is in the title bar (<kbd>Ctrl+E</kbd>), with **Back** and **Forward** buttons beside it. In a very narrow window it becomes a magnifier button that opens the box.
- **More options** (⋯) above the list has Search messages, Commands, Refresh conversations, the Help guide, the tour, the keyboard shortcuts and Settings. **New chat** is next to it.

In a narrow window (under 800 pixels wide) Message Link shows one pane at a time, the list or the conversation, with a **Back to conversations** button. You can also collapse the list to a strip of photos: see [Compact list](#compact-list).

### Connecting your services {#connecting}

Open **Settings** (<kbd>Ctrl+,</kbd>). It opens on **Accounts**, where each service has a card with a row for each account and its status: **Connected**, **Signed in** (a saved sign-in that isn't connected right now; Google Messages says **Paired**) or **Not connected**.

- **Google Messages**: choose **Pair**. On your phone, open Google Messages, tap your account picture, then choose **Device pairing**. Sign in with the same Google account in the window that opens, then tap the emoji on your phone that matches the one Message Link shows. Your phone then lists this PC as **Message Link**. It needs an Android phone; an iPhone can't pair.
- **Messenger**: choose **Connect** and sign in to Facebook in the window that opens, then choose **Done**. Encrypted conversations turn on by themselves after a new sign-in.
- **Instagram**: choose **Connect** and sign in with your Instagram username and password. *Log in with Facebook* isn't supported.
- **Google Voice**: choose **Add Google Voice account** and sign in at voice.google.com. Add more numbers with **Add another Google Voice account**; each signs in separately.

On a PC that uses a Message Link Server, you do this on the server's page instead; see [Signing in on the server](#server-sign-in).

Each service's details, limits and fixes are in [What each service can do](#services).

### Your first run {#first-run}

- **The welcome** is the first screen until something is set up. It says what Message Link is and offers three ways in:
  - **Sign in on this PC** opens Settings → Connections to sign in here; see [Connecting your services](#connecting).
  - **Connect to a Message Link Server** joins a server you already run, which keeps your accounts connected on a computer at home, for this PC and your phone alike; see [Using a Message Link Server](#messages-server).
  - **Try sample conversations** shows Message Link with made-up people; see [Sample conversations](#sample-conversations).
- **Until something is set up**, whatever needs an account says so where you're looking, with **Sign in on this PC** beside it: **New chat** (<kbd>Ctrl+N</kbd>), search, the tray menu's **New chat** and the taskbar's tasks. The tray's **Open Message Link** brings back the welcome, and **Settings** opens Settings.
- **Once an account connects** or you join a server, the welcome is gone for good. If you later sign out of everything, the conversation list offers **Add an account** instead.
- **The welcome's last line** says Message Link is also available for Android, with a Message Link Server; **Learn more** opens the Message Link website.
- **The title bar** has no search or navigation controls until something is set up, because they act on your conversations.
- **The tour** is offered once: after your first account connects and your conversations are on screen, or while you look around the sample conversations. It never runs over the welcome. **Not now** skips it; you can take it any time from **More options → Take the tour**, the command palette, Settings → About, or this guide.
- **Closing the window** the first time leaves Message Link running in the system tray, and a quiet notification says so: "Message Link is still running." Choose **Quit Message Link** from the tray icon to exit.
- **After your first account connects**, Message Link asks once whether to start when you sign in to Windows. It starts in the tray, so new messages reach you before you open it.
- **If Message Link closed unexpectedly** last time, a notice says so with **Save logs**, in case you want to send them to whoever helps you.

### Sample conversations {#sample-conversations}

**Try sample conversations** on the welcome lets you try Message Link before connecting anything.

- Message Link closes and opens again over made-up people and chats on all four services: friends, family and groups, one person on two services linked into one conversation, three labels, and archived and spam conversations. A banner across the top says "Sample conversations — nothing here is real".
- **Nothing is sent.** The message box says so instead of sending, and a reaction, a new chat or a new group says the same rather than happening. Settings won't sign in to a service, join a server, restore a backup or check for updates here.
- **Your own data stays closed.** The sample keeps its own temporary folder, which is removed when it ends. It shows no notifications, has no tray icon or taskbar menu, and doesn't start with Windows.
- **Leave sample** in the banner opens Message Link again on the welcome. Closing the window ends the sample too.

### Getting help {#getting-help}

- <kbd>F1</kbd> opens this guide from the main window or the chat console. Search it with the box above the contents (<kbd>Ctrl+F</kbd> or <kbd>Ctrl+E</kbd> there), and go back to the previous chapter with <kbd>Alt+Left</kbd>.
- <kbd>Ctrl+/</kbd> lists the keyboard shortcuts, with a few mouse tricks.
- **More options → Take the tour** replays the tour.
- [What each service can do](#services) lists what each service can and can't do.
- In the chat console, `/help` lists commands and `/help command` shows a command's manual page.
- **Settings → Troubleshooting** has the diagnostic logs and Connection health; see [Troubleshooting](#troubleshooting).

## 2. Keyboard shortcuts {#keyboard-shortcuts}

Press <kbd>Ctrl+/</kbd> any time for the list inside the app. Shortcuts don't run while a dialog or the photo viewer is open.

### Anywhere in the main window {#keys-main}

| Keys | What it does |
|---|---|
| <kbd>F1</kbd> | Open this guide |
| <kbd>Ctrl+N</kbd> | New chat |
| <kbd>Ctrl+K</kbd> | Commands: do anything by name |
| <kbd>Ctrl+E</kbd> | Search (the box in the title bar) |
| <kbd>Ctrl+F</kbd> | Search in the open conversation |
| <kbd>Ctrl+U</kbd> | Show only unread conversations, or all again |
| <kbd>Ctrl+Tab</kbd> / <kbd>Ctrl+Shift+Tab</kbd> | Switch between recent conversations |
| <kbd>Alt+Up</kbd> / <kbd>Alt+Down</kbd> | Previous or next chat in the list |
| <kbd>Alt+Shift+Up</kbd> / <kbd>Alt+Shift+Down</kbd> | Previous or next unread conversation |
| <kbd>Alt+Left</kbd> / <kbd>Alt+Right</kbd> | Back or forward through conversations you opened |
| <kbd>Ctrl+1</kbd> … <kbd>Ctrl+9</kbd> | Your pinned chats, in list order |
| <kbd>Ctrl+Shift+M</kbd> | Mute or unmute this conversation |
| <kbd>Ctrl+Shift+P</kbd> | Pin or unpin this conversation |
| <kbd>Ctrl+Shift+N</kbd> | Open this conversation in its own window |
| <kbd>Ctrl+W</kbd> | Close a conversation window |
| <kbd>Ctrl+Shift+I</kbd> | Show or hide service badges |
| <kbd>Ctrl+`</kbd> | Chat console (the key left of 1, on any keyboard layout) |
| <kbd>Ctrl+,</kbd> | Settings |
| <kbd>Ctrl+/</kbd> | Keyboard shortcuts |

### Writing a message {#keys-composer}

| Keys | What it does |
|---|---|
| <kbd>Enter</kbd> / <kbd>Shift+Enter</kbd> | Send / new line |
| <kbd>Ctrl+Shift+Enter</kbd> | Send later: choose a time |
| <kbd>Up</kbd> | Edit your last message, when the message box is empty |
| <kbd>Esc</kbd> | Cancel a reply, an edit or a recording |
| <kbd>Alt+Down</kbd> | Change service, while writing in a merged conversation |
| <kbd>Ctrl+V</kbd> | Paste a photo |
| <kbd>Ctrl+Shift+R</kbd> | Record a voice message, or stop recording |
| <kbd>Win+.</kbd> | Windows emoji panel |

### Elsewhere {#keys-elsewhere}

| Where | Keys |
|---|---|
| Photo viewer | <kbd>Left</kbd> / <kbd>Right</kbd> previous or next photo, <kbd>F11</kbd> full screen, <kbd>Esc</kbd> close the photo |
| Photos waiting to send | <kbd>Left</kbd>/<kbd>Right</kbd> move between them, <kbd>Alt+Left</kbd>/<kbd>Alt+Right</kbd> move the selected one, <kbd>Home</kbd>/<kbd>End</kbd> first or last, <kbd>Delete</kbd> remove |
| Command palette | <kbd>Up</kbd>/<kbd>Down</kbd> choose, <kbd>Enter</kbd> run |
| The divider beside the list | <kbd>Left</kbd>/<kbd>Right</kbd> resize the list in small steps |
| Pop-out windows | <kbd>Ctrl+W</kbd> closes it; <kbd>Ctrl+F</kbd>, <kbd>Ctrl+Shift+M</kbd>, <kbd>Ctrl+Shift+P</kbd> and <kbd>Ctrl+`</kbd> work there too |
| Chat console | See [Console keys](#console-keys) |
| This guide | <kbd>Ctrl+F</kbd> search, <kbd>Alt+Left</kbd> back, <kbd>Esc</kbd> clear the search or close |
| Anywhere in Windows | <kbd>Ctrl+Alt+Shift+M</kbd> shows Message Link (see [Global shortcut](#global-shortcut)) |

> **Tip:** Ctrl+Shift+M, Ctrl+Shift+P and the palette's Mute show a short "Muted", "Unmuted", "Pinned" or "Unpinned" note. A mute set this way lasts until you unmute.

## 3. Getting around {#getting-around}

### Commands (Ctrl+K) {#command-palette}

<kbd>Ctrl+K</kbd> opens **Commands**: type part of what you want and press <kbd>Enter</kbd>. Every word you type must appear in the command's name; up to 40 matches show, with their shortcut on the right. It offers:

- **Search messages**, **Chat console**, **Saved messages**, **New chat**, **Open unread**, and **Quick replies…**
- **Settings**, and each Settings page by name: *Settings → Connections*, *Notifications*, *Messaging*, *Appearance*, *Startup and tray*, *Storage and backup*, *Troubleshooting*, *About*.
- **Check for updates**, **Connection health**, **Show service badges** or **Hide service badges**, **Keyboard shortcuts**, **Help guide** and **Take the tour**.
- **Pause notifications for 1 hour**, **Pause notifications until tomorrow morning**, or **Resume notifications** while paused.
- **Open Instagram in browser**, and the same for Messenger, Google Messages and Google Voice.
- **New label…** and **Show label: *name*** for each of your labels.
- With a conversation open: **Conversation details**, **Shared media, links, files and audio**, **Attach a photo or file**, **Add label** or **Remove label** for each label, and **Insert quick reply: *name***, which puts the text in the message box without sending it.
- For every conversation: **Message *name* · *service*** and **Mute** (or **Unmute**) ***name***. Type a few letters of a name to find them.

### Recent conversations (Ctrl+Tab) {#recent-switcher}

<kbd>Ctrl+Tab</kbd> works like Alt+Tab for conversations.

- A quick tap (letting go of Ctrl at once) jumps straight back to the conversation you had before.
- Hold <kbd>Ctrl</kbd> and a strip of up to seven cards appears. Each <kbd>Tab</kbd> moves along it, <kbd>Shift</kbd> goes back, and letting go of Ctrl opens the highlighted one. Clicking a card opens it too.
- <kbd>Esc</kbd>, any other key or switching window cancels without opening anything.
- It remembers your last ten conversations, only in memory, and only those the list is showing now. Passing over one never opens it or marks it read.

### Back and forward {#back-and-forward}

<kbd>Alt+Left</kbd> and <kbd>Alt+Right</kbd>, or the arrows beside the search box, retrace the conversations you opened, like a browser. Message Link remembers up to 100 steps and skips conversations that no longer exist.

### Jumping {#jumping}

- <kbd>Alt+Up</kbd> / <kbd>Alt+Down</kbd> walk the list one chat at a time and stop at the ends.
- <kbd>Alt+Shift+Up</kbd> / <kbd>Alt+Shift+Down</kbd> jump to the previous or next unread conversation, including ones you marked unread. At the end, "No more unread conversations in this view."
- <kbd>Ctrl+1</kbd> … <kbd>Ctrl+9</kbd> open your pinned chats in the order you arranged them.

## 4. Your conversation list {#conversation-list}

### Rows {#rows}

- The **name** is bold while there's something unread. The **time** reads "Just now", "5 min ago", "2 hr ago", "Yesterday", then a date, and keeps itself fresh.
- The **preview** shows the newest message: "You: …" for yours, the sender's name in groups. It becomes **typing…** while someone types (not on Google Voice) and **Draft: …** while you have unsent text there.
- **Pills** beside the time: **Pinned** first, then your labels. At most two show; with more, the second becomes "+2" and its tooltip names the rest.
- **Small icons**: a red phone for a missed call, a grey phone for other calls, a voicemail icon, a muted bell (its tooltip says until when), and a bell for **Remind me later** ("Snoozed until …").
- A small spinner replaces the time while a row updates; a thin bar shows while the list loads.
- A merged conversation says **Merged** under its name instead of a service badge.

### Narrowing the list {#filters-and-views}

Two dropdowns sit above the list.

- **View**: **Messages**, **Unread** (<kbd>Ctrl+U</kbd>), **Archive**, **Spam** and **Drafts**; then your labels, once you have one: **All labels**, a tick for each label (choose several and a chat with any of them shows) and **Manage labels…**; then **Saved**, which opens your [saved messages](#saved-messages). With more than eight labels they fold into a **Labels** submenu. While a label is chosen the dropdown names it after its colour dot, for example "Work", "Unread · Work" or "2 labels".
- **Services**: **All services**, then each service, or each account when you have several of one (Voice numbers are listed separately). Tick several at once. Unread counts sit on the right; they leave out muted, archived and spam conversations. With Google Voice connected there's also **Type**: All types, Messages, Calls or Voicemails (only **Calls** lists calls you answered or made). **Clear filters** appears while a filter is on, and **Show service badges** (<kbd>Ctrl+Shift+I</kbd>) is here too.

A dropdown that's filtering says so, for example "Services (2)", in your accent colour. Filters look only at conversations already loaded: **Load more conversations** at the bottom of the list fetches older ones. When a filter hides everything, the list offers **Clear filters** and **Load more conversations**.

### The conversation menu {#conversation-menu}

Right-click a conversation, or use the **⋯** button in its header. You'll see, depending on the conversation:

1. **Open in new window**, or **Move to main window** in a pop-out. In a pop-out, **Keep on top**.
2. **Shared content**, **Details** and **Export conversation…**
3. **Merge with another conversation…**, or on a merged one **Merged conversations**, which holds **Add a conversation…**, **Remove a conversation…** and **Unmerge conversations…** See [Merged conversations](#linked-conversations).
4. **Pin** or **Unpin**.
5. **Mute**: For 1 hour, For 8 hours, For 1 week, or Until I unmute. When muted it reads **Unmute (muted until …)**.
6. **Labels**: tick your labels, **New label…** or **Manage labels…**
7. **Remind me later**: In 30 minutes, In 1 hour, In 3 hours, or At 6:00 PM (At 8:00 AM tomorrow when 6 PM is near). With a reminder set it reads **Cancel reminder**.
8. **Mark as unread** (on a chat with nothing unread) or **Mark as read** (on one you marked unread).
9. **Send read receipt**, after [private reading](#private-reading) kept a chat read in Message Link only.
10. **Choose contact photo…** (or **Choose conversation photo…** for a group), and **Remove custom photo**.
11. **Block and report spam…**, for one person's Google Messages chat, or **Not spam** for a Google Messages or Google Voice chat in Spam.
12. **Archive** or **Unarchive**. Instagram's reads **Archive in Message Link**.
13. **Delete…**, where the service allows it: Google Messages ("will be deleted from your phone"), Messenger ("deleted from Messenger, along with the history Message Link keeps of it") and Google Voice ("deleted from your Google Voice account"; the people in it keep theirs).

### Compact list {#compact-list}

The button at the left of the title bar collapses the list to a slim strip of photos with service badges and unread dots; hover for the name. The **Conversation list options** button keeps New chat, the View (with your labels) and Services menus and the ⋯ items within reach; a coloured dot on it means a label filter is on. Drag the divider between the list and the conversation to set its width (240 to 640 pixels); Message Link remembers it, along with the window's size and whether it was maximized.

### When Message Link opens {#on-launch}

A normal launch opens your first conversation once. A start with Windows, which runs in the tray, waits until you first open the window yourself. Opening from a notification or the console opens what you asked for instead.

## 5. Organizing conversations {#organizing}

Pins, mutes, labels, reminders, unread marks and the names and photos you give conversations stay **in Message Link**. They never change anything in the services or their own apps. With a Message Link Server, your other devices that use it share them.

### Pins {#pins}

- Pin a chat to keep it at the top of every view except Archive and Spam.
- **Drag a pinned chat** to reorder your pins. A line shows where it will land, and the list scrolls when you drag near its edge.
- <kbd>Ctrl+1</kbd> … <kbd>Ctrl+9</kbd> open pins in your order. The [taskbar jump list](#taskbar) shows up to nine pins alphabetically.

### Labels {#labels}

- **New label…** (from a conversation's menu, or Commands) asks for a name of up to 32 characters and one of eight colours: Red, Orange, Yellow, Green, Teal, Blue, Purple or Pink. A label made from a conversation's menu goes on that conversation at once.
- **Manage labels…** renames (type and press Enter), recolours and deletes labels. Deleting says how many conversations will lose it.
- Filter the list by labels from the view dropdown above the list (it reads **Messages** until you pick one), or **Show label: *name*** in Commands.

### Muting {#muting}

- Mute for 1 hour, 8 hours, 1 week, or until you unmute. A muted chat shows a bell; hover it to see when the mute ends.
- A timed mute ends by itself, even across sleep, and the conversation notifies again.
- Muted conversations don't count towards unread badges.

### Remind me later {#remind-me-later}

- Choose a time and the conversation stays quiet until then. At that time you get **one reminder** about whatever is still unread. Read it meanwhile and there's no reminder.
- Windows delivers the reminder even if Message Link is closed by then.
- Notifications have the same thing as **Remind me in 1 hour**.

### Archive and spam {#archive-and-spam}

- **Archive** moves a conversation out of your inbox. On Google Messages, Messenger and Google Voice it archives on the service too. Instagram can't be archived from outside its own app, so Message Link hides it **in Message Link only**, and merged conversations are archived in Message Link only.
- An archived conversation comes back by itself when a new message arrives.
- **Spam** holds Google Messages' Spam & blocked folder, Google Voice's spam and Instagram's filtered chats. Spam never notifies and never counts as unread.
- **Block and report spam…** blocks a Google Messages number on your phone, reports it to Google and moves the chat to Spam. Google Messages has no way to mark spam without blocking. Group chats can't be blocked from Message Link, and the other services have no "mark as spam" here.
- **Not spam** moves a Google Messages or Google Voice chat back, and unblocks a Google Messages number.

### Saved messages {#saved-messages}

- **Save** in a message's menu keeps a copy in **Saved Messages**. Open the list from the View dropdown (**Saved**), Commands, or a conversation's details, where it shows only that conversation.
- Search them, **Copy**, **Unsave**, **Cancel reminder** or **Open conversation**, which opens the original message in its place.
- Text and attachment details are kept in Message Link. Saving a message keeps full-size copies of its photos, so they still open if the service no longer has them. Unsaving removes them.

### Message reminders {#message-reminders}

- **Remind me** in a message's menu: In 1 hour, This evening at 8:00 PM, Tomorrow morning at 8:00 AM, or **Choose a time…**
- Setting a reminder also saves the message. At the time, a notification opens the conversation with **that exact message highlighted**, even if Message Link had been closed.

### Your own names and photos {#custom-names}

- **Under a conversation's title** is the person's number (an RCS number first, then how many more they have), or their username where the service has no number, and a group's size. When it has more than one service, the small arrow beside it lists them, each with a dot in your message colour for it, and shows which one your next message uses; point at the arrow or click it. With one service, point at the number for its name, and at a linked conversation's number for every number and service.
- **Right-click a conversation's title** in its header for **Name this conversation**, plus Copy name, Copy number and Copy account name. Your name is kept in Message Link only, up to 128 characters. A person's name follows them across their one-to-one chats; a group keeps its own. **Remove name** puts the service's back. (One-to-one Google Messages chats take their names from your phone's contacts instead.)
- **Add to contacts…** in the same menu, or when you right-click a conversation in the list, saves a number that isn't in your contacts yet (one shown as a number, or one you named here). **New contact** takes first and last name, phone and its type, and a company if you like; **Existing contact** finds someone on your phone by name and adds the number to them, for a second number. Your phone saves it to its Google account, and Google Messages and Google Voice then show the name on every device. It's offered while a phone joined to your Message Link Server has **Save contacts from your PC** on (Message Link on your phone → Settings → Contacts), and the dialog names that phone and its Google account. If the phone is away, it saves the contact when it next connects and Message Link tells you then; a number already in your contacts is never added twice. **Existing contact** needs the phone to answer within a few seconds. On the phone, **Add to contacts** is in a conversation's menu (long-press it in the list, or ⋮ in the conversation) and opens your phone's Contacts app, where **Add to existing** adds it to someone you have and you can add a photo.
- **Choose contact photo…** in the conversation menu sets your own picture (PNG, JPG or WEBP); **Remove custom photo** undoes it. While a phone saves contacts, the PC then asks whether to put the photo on (or take it off) the person's contact too, so Google Messages and your phone show it. The person has to be in your contacts already.

## 6. Writing and sending {#writing-and-sending}

### The message box {#composer}

- <kbd>Enter</kbd> sends and <kbd>Shift+Enter</kbd> starts a new line. The box grows to about eight lines, then scrolls, and eases back to one line after you send.
- The **Send** button appears once there's something to send, filled with the colour your message will have. With an empty box it becomes a **microphone** where voice messages are supported.
- **Emoticons become emoji** as you type: `:)` `:(` `;)` `:D` `:P` `<3`, with or without a nose (`:-)`). `:D` and `:P` change after the next space. Nothing changes inside a word, a link or a time, or while you hold Ctrl.
- The placeholder says how a Google Messages text will go: "RCS message", "Text message · SMS", "Add a message · MMS". While you type, a small label beside the box shows the route. Your phone has the last word: it may switch route, or turn a long text into MMS.
- Your bubble's colour follows the route too: RCS and SMS have different shades. You can change the colours in [Appearance](#settings-appearance).
- Each service's length limit applies. A message that's too long greys the Send button.
- Press <kbd>Enter</kbd> before a conversation has finished connecting and Message Link sends as soon as it can. Editing the text or switching conversation meanwhile cancels that.
- Google Messages conversations show a small amber status under the title while something is wrong with the phone link: "Reconnecting to phone…", "Phone unavailable", "Renewing Google sign-in…".

### Drafts {#drafts}

- Unsent text saves itself a moment after you stop typing, encrypted, and is waiting when you come back, even after a restart. A reply you'd chosen is kept with it. With a Message Link Server, your other devices that use it share your drafts.
- The list shows **Draft: …** in that conversation's preview, and the **Drafts** view lists them all.
- **Settings → Storage and backup → Saved drafts** clears every draft at once.

### Emoji, GIFs and quick replies {#emoji-gifs-replies}

The emoji button beside Send opens a picker with three tabs.

- **Emoji**: search (<kbd>Enter</kbd> inserts the first match), browse by category, choose a skin tone, and find your 32 most recent under **Recent**. Pick several in a row.
- **GIF**: appears once you've saved a GIPHY key in [Settings → Messaging](#settings-messaging). Trending GIFs come first; search, then **More GIFs**. A chosen GIF waits like a photo. GIFs over 20 MB are refused.
- **Replies**: your quick replies. Choosing one puts its text in the box at the cursor; it **never sends by itself**. **Manage quick replies…** (or **Quick replies…** in Commands) keeps up to 50, each with a name of up to 40 characters and up to 2,000 characters of text.

### Send later {#send-later}

- Right-click **Send**, or press <kbd>Ctrl+Shift+Enter</kbd>, and choose: **Send in 1 hour**, **Send this evening, 8:00 PM** (before 7 PM), **Send tomorrow morning, 8:00 AM**, or **Choose a time…** for a date and time in five-minute steps.
- The message appears at the end of the conversation. Its footer says **Scheduled for 8:00 PM**, with **Send now**, **Edit** (back into the box, unsent) and **Cancel** links. Right-click it for the same, plus Copy.
- Send later takes text and replies, not photos, and isn't offered while you edit a message. Up to 200 messages can be scheduled.
- With a Message Link Server, the server sends it at its time, even with Message Link closed everywhere. Without one, **Message Link has to be running** at the time. If it was closed, the message goes when Message Link next starts, but only within about ten minutes of its time; after that it stays in the conversation, clearly **Not sent — Message Link was closed at …**, for you to send or cancel. Nothing goes without you knowing. A scheduled message only ever sends from the account it was scheduled on.

### Undo send {#undo-send}

- Turn on **Undo send** in [Settings → Messaging](#settings-messaging): **5 seconds** or **10 seconds**.
- A message you send then waits with an **Undo** link; with *Delivery status as text* on it reads "Not sent yet · Undo takes it back". **Undo** puts the text, and its reply, back in the box.
- Only text from the message box waits. Photos, files, voice messages, scheduled sends and the console's sends go at once. Quitting doesn't send anything still waiting: it goes back to its conversation's draft. So does anything waiting when the PC crashes or loses power, the next time Message Link opens.

### Edit your last message {#edit-message}

- On Messenger and Instagram, press <kbd>Up</kbd> in an empty message box to edit your newest message. It must be text only and already confirmed by the service.
- An **Editing message** bar appears, the message is tinted, and Send becomes a tick. <kbd>Enter</kbd> saves, <kbd>Esc</kbd> cancels, <kbd>Shift+Enter</kbd> adds a line. Your draft is set aside and comes back afterwards.
- **Edit message** in a message's menu edits any of your own text messages on those services. Edited messages are labelled **Edited**. The new words show the moment you save; if the service turns the edit down, the message goes back to what it said and a notice says why.
- An edit can't leave a message empty. If Message Link couldn't confirm an earlier change to a message, it won't change it again until you've checked.

### Safety nets {#safety-nets}

Message Link never sends anything twice behind your back.

- If a send fails, a **Message wasn't sent** banner offers **Restore message**, which puts the text back to edit or try again. The message itself reads "Not sent · Restore draft to try again".
- "**Not confirmed yet** · It won't be sent twice" means the service hasn't answered yet. **Check again** in the message's menu asks it, without resending.
- "**May not have sent** · Check the conversation before sending it again" means Message Link can't know. It never resends; look at the conversation on another device first.
- If Message Link closed while messages were still unconfirmed, the next start says so: "A message sent at 3:42 PM wasn't confirmed before Message Link closed."
- At most 100 sends can be waiting for confirmation at once.

## 7. Photos, files and voice messages {#media}

### Attaching {#attaching}

- The **+** button (**Photo or file**), **drag and drop** onto the conversation ("Drop to attach"), <kbd>Ctrl+V</kbd> for a picture or for files copied in File Explorer, the GIF tab, or **Send to → Message Link** in File Explorer, which waits in the conversation you choose without sending.
- Up to **10 photos** at once, or one video or file. Each item up to 50 MB, a photo set up to 100 MB. Photos can wait unsent in up to four conversations at a time.
- An attachment can't go with a reply: cancel the reply first.

### Photos waiting to send {#staged-photos}

- Photos wait as thumbnails above the box until you press <kbd>Enter</kbd>. The order you see is the order they're sent.
- Click one to view it. Drag to reorder (mouse or pen), or select one and press <kbd>Alt+Left</kbd>/<kbd>Alt+Right</kbd>. **×**, <kbd>Delete</kbd> or <kbd>Backspace</kbd> removes it. Right-click a thumbnail for View, Move left, Move right and Remove.
- Text you add goes as a caption where the service supports it, otherwise as its own message after the photos. On Google Messages each photo is its own message.
- While an attachment is sending, <kbd>Enter</kbd> queues what you've written to go after it. If the attachment doesn't send, your words come back to the box unsent.

### How photos show {#photo-display}

- One photo shows large; two or three as an overlapping strip; four or more as a pile with an **N photos** badge. Click to open the viewer.
- Right-click a photo for **Copy image**. Copied images, like copied codes, are kept out of Windows clipboard history.
- Google Messages photos come from your phone, and may say "Getting the full-size photo from your phone", "Your phone is still downloading this photo" or "Auto-download is off on your phone". A photo that failed shows **Retry**.

### The photo viewer {#media-viewer}

- It opens in its own window, growing out of the photo you clicked.
- **Zoom** up to 8× with the slider or the mouse wheel, and drag to pan when zoomed.
- <kbd>Left</kbd>/<kbd>Right</kbd> move through the conversation's photos, loading earlier ones at the start. <kbd>F11</kbd> or a double-click goes full screen; <kbd>Esc</kbd> closes.
- **Download** saves the original. Videos play with normal controls. The controls fade after three seconds without moving the mouse.
- Older Messenger attachments may take a moment to reopen.
- Looking at a photo never marks a conversation read.

### Saving {#saving-attachments}

On Android, saved photos and videos go into the **Message Link** album in Pictures or Movies. Existing saved files stay where they are.

**Save attachment** in a message's menu saves a photo or file; **Save photo** / **Save N photos** on the hover toolbar saves a set into a folder you choose and says how many were saved.

Saved files are marked as coming from the internet, as a browser marks a download, so Windows checks them before they open. A photo in a set always saves as a picture, whatever its name says.

### Cards {#cards}

- **Contact cards** show the name, number and email, with **Message** and **Copy number**.
- **Files** show their type and a **Download** button. A program or shortcut always shows as one, whatever type the sender gave it.
- **Links** become a preview with a picture and description, unless you turn off *Link previews*.
  Previews of recent links are ready when you open a conversation, including links you send from your phone. None are fetched for a conversation in Spam.
- Fetching a preview tells the link's site that the message was opened, and from where. So a link from someone you don't know shows a small **Load preview** instead, and fetches nothing until you click it. Someone counts as known when they're a saved contact on your phone (Google Messages) or you've written in that conversation. With [private reading](#private-reading) on for a service, every link there waits for **Load preview**, except your own.
- A link whose words read as one web address but would open another shows as plain text, so what you click is what you read.
- **Shared reels and posts** from Instagram and Facebook show a preview with **Open in Instagram** or **Open in Facebook**. A card Facebook gives a button of its own, such as a Marketplace listing's **View listing**, shows that button too; clicking anywhere on the card opens it.
- A message of one to three emoji shows them large, without a bubble.

### Voice messages {#voice-messages}

- On Google Messages and Messenger, click the microphone (empty box) or press <kbd>Ctrl+Shift+R</kbd>. A bar shows a level meter and a timer, up to **5 minutes**. <kbd>Esc</kbd> cancels; **Stop** ends it.
- The recording then waits in the box so you can play it back, or delete it, before sending.
- If Windows blocks the microphone, Message Link says so with **Open microphone settings**.
- Received voice messages play in place with a waveform, a slider, a **speed button (1×, 1.5×, 2×)** and Save. One plays at a time.
- Instagram and Google Voice can't send voice messages; Instagram's received ones play.

## 8. Working with a message {#message-actions}

Hover a message for a small toolbar: **Reply**, **React**, **Save photo** (when there are photos), **More actions** (⋯) and the time. Right-click a message, or choose ⋯, for its menu. Items appear only where they apply:

| Item | What it does |
|---|---|
| **Copy link** | Copies the link you right-clicked |
| **Copy selection** | Copies the words you selected |
| **Copy code 482913** | Copies a one-time code from an incoming message in one click. It only appears when the message clearly holds a code, never a phone number, an amount or a date |
| **Copy** | Copies the message's text |
| **Forward…** | Choose a conversation; the text and any photos land in its message box for you to send |
| **Save** / **Unsave** | Keeps it in [Saved Messages](#saved-messages) |
| **Remind me** | A notification about it later; see [Message reminders](#message-reminders) |
| **Save attachment** | One for each photo or file |
| **Edit message** | Your own text, on Messenger and Instagram |
| **Delete for me** | Removes your copy; others keep theirs |
| **Delete for everyone** | Asks the service to remove it for everyone |
| **Reply** | Replies to this exact message |
| **Go to original message** | Jumps to what a reply quoted |
| **React** / **Remove my reaction** | Emoji reactions |
| **Check again** | Asks the service whether an unconfirmed send went through |
| **Message details** | Sent time, status, route, encryption and who read it, with a Technical details section |

### Replies {#replies}

- Choose **Reply** and a bar shows what you're replying to; **×** or <kbd>Esc</kbd> cancels. Replies are text.
- A reply shows a quote above it. **Click the quote** and the conversation scrolls to the original and highlights it, loading up to three earlier pages to find it (not on Instagram, where you scroll up first).
- Google Messages offers replies only in RCS chats; Google Voice has none.

### Reactions {#reactions}

- **React** shows the service's emoji: Google Messages 👍 ❤️ 😂 😮 😢 👎, Messenger 👍 ❤️ 😆 😮 😢 😡 (one each, so a new one replaces yours), Instagram ❤️ 😂 😮 😢 😡 👍.
- Each reaction sits in a small round badge on the message's bottom corner, with a count when several people chose the same one. Click one to add the same or remove yours; hover to see who reacted.

### Deleting {#deleting}

- **Delete for me** and **Delete for everyone** each ask first, showing the message. Once you confirm, the message goes (or reads "Message deleted", for everyone) at once; if the service turns it down, it comes back and a notice says why.
- Per service: Google Messages has Delete for me only (it removes the message from your account everywhere, your phone included). Messenger has Delete for everyone on your own messages and Delete for me (not in encrypted chats). Instagram has only Delete for everyone, on your own. Google Voice has neither.

## 9. Reading a conversation {#reading}

### Layout {#layout}

- Messages are grouped by sender under a day divider ("Today", or the date), with a time above them after ten quiet minutes. In groups each sender's name shows. Hover any message for its time and status.
- While you scroll, a **date pill** shows at the top and fades when you stop.
- On Messenger and Instagram, a **New messages** line marks where you stopped reading when you opened the conversation. It disappears once you send. Google Messages and Voice don't say where you stopped, so they can't have one.
- Links and email addresses are clickable; hover a link for its full address. Message text can be selected and copied.
- **Encryption**: hovering the newest message shows a lock when it was end-to-end encrypted, and Google's "RCS chat with …" note reads "Private chat with …". SMS and MMS never show a lock.

### Delivery and read status {#receipts}

- The last message you sent says how it's doing in words: "Sending…", "Sent", "Delivered" or "Read". Once a reply comes after it, the status goes if the message was delivered, and stays while it says "Sent".
- Click any message you sent to show its status, and click again to put it away.
- Turn off **Delivery status as text** in [Appearance](#settings-appearance) for icons instead: a clock while sending, one check sent, two checks delivered, all in grey; two white checks read (black in a light theme). A red alert means failed, an amber question mark uncertain. "Not confirmed yet", "May not have sent" and "Edited" always show as words.
- In groups, small **read faces** sit under the newest message each person has read, and slide down as they read on. Hover for "Read by …".
- Receipts appear only where the service reports them. Google Voice shows "Sent"; SMS never reports Read.

### Typing {#typing}

- When someone types, their photo peeks out above the message box with three moving dots (up to three photos in a group). Hover for "Sam is typing…".
- Your own typing is sent on Google Messages, Messenger and Instagram, unless [private reading](#private-reading) is on for that service.

### Scrolling {#scrolling}

- The conversation follows new messages while you're at the bottom; scroll up and it stays put. A **Latest** pill appears, becoming **N new** as messages arrive. Click it to jump down.
- Scrolling near the top loads earlier messages without losing your place. Message Link keeps up to 1,000 recent messages per conversation; older ones are still found by [search](#search).
- Search results, saved messages, reminders and the gallery **open the conversation at that exact message**, highlighted. That never marks it read.

### When a conversation is marked read {#read-state}

- Half a second after you reach the bottom, and only while the window is in front with nothing covering it (Settings, New chat, the viewer, a dialog).
- Never while you're looking at an older stretch opened from search.
- After a notification or a share brings Message Link forward, it waits until you click, type or scroll.
- With [private reading](#private-reading) on for that service, it's marked read in Message Link only.
- **Mark as unread** in the conversation menu marks it unread in Message Link.

### Calls and voicemail {#calls-and-voicemail}

Google Voice calls appear as cards rather than a text conversation.

- A **missed call** or **voicemail** gets a row in the list. A call you answered or made, or one nobody picked up, doesn't, and never counts as unread: choose **Calls** under **Type** in the Services dropdown to see every call, or search for it. The phone app hides them too; searching there finds them.
- A coloured badge (red missed, blue voicemail, green connected), a headline such as "Missed voice call", and the full date.
- **Voicemail** shows Google's transcript and a player. The audio downloads only when you press play. **Save** keeps it as a file named after the caller and the date.
- **Send a message to *name*** opens the text conversation. If both Google Voice and your phone's Google Messages can reach them, Message Link asks which to use.
- Message Link can't place or answer calls.

### Details {#details-panel}

Click the conversation's title, or **Details** in its menu, for a panel beside the conversation (over it in a narrow window):

- the photo and **Accounts** (**People** in a group), with copy buttons for names, usernames and numbers;
- counts of **Media**, **Links**, **Files** and **Audio** in what's loaded, each opening the gallery;
- **Search conversation**, **Saved messages** and **Mute notifications**. A Messenger conversation muted on your phone shows as muted; **Unmute notifications in Message Link (muted in Messenger)** leaves it muted in Messenger;
- for a merged conversation, a suggestion when the same person is also on another service;
- for an ordinary Messenger group: **Rename group…**, **Add people…** and the **People** list, with **Remove** if you're one of its admins. Each asks first, because everyone in the group sees the change. End-to-end encrypted groups can't be changed here.

### Shared content {#shared-content}

- **Shared content** in the conversation menu has tabs for **Media**, **Links**, **Files** and **Audio**.
- Media is a grid grouped by Today, Yesterday, This week, This month and then by month. Sort **Newest first** or **Oldest first**, and show All, Photos or Videos. It loads earlier items as you scroll, or with **Load earlier**.
- Links open with a click; right-click for **Open link**, **Copy link** or **Go to message**.

### Conversations in their own window {#pop-out-windows}

- **Open in new window** (<kbd>Ctrl+Shift+N</kbd>) moves a conversation into its own window, with its draft. A conversation is open in one place at a time.
- **Keep on top** in its menu keeps it above other windows (not remembered). **Move to main window** brings it back.
- The main window says "This conversation is open in its own window." with **Show window** and **Bring it back here**.
- Windows open when you quit come back next time, behind the main window and without marking anything read, on a monitor that still exists.

## 10. Merged conversations {#linked-conversations}

When the same person is on several services, you can merge your one-to-one conversations with them into one history in Message Link. Nothing changes on the services, and you can unmerge any time.

- **Merge with another conversation…** in the conversation menu opens **Merge conversations**: give it a name if you like, then pick from **Suggested** and **All conversations** (search with "Find a conversation"). Matching Google Voice calls and voicemail are included. Up to eight conversations can be merged. If the phone numbers differ, Message Link warns you to merge only if they're the same person.
- The same dialog also lists your **Merged conversations**. Choose one to add this chat to it; the merged conversation keeps its name and settings. A merged conversation is found by any of its conversations' names.
- One timeline shows everything in order, each message tagged with its service. **Show one service** in the header filters it to one, on this PC only.
- **Sending**: a reply goes out where the person last wrote to you. The **Send using** button beside the box (<kbd>Alt+Down</kbd>) picks another service; tick **Always send with *service* to *name*** to keep that choice. Replying to a particular message always uses that message's service.
- **Add a conversation…**, **Remove a conversation…** (it goes back to the list with its messages and drafts) and **Unmerge conversations…** are under **Merged conversations** in the menu.
- Pins, mutes, labels and reminders apply to the merged conversation as a whole. Archiving it stays in Message Link, and it can't be deleted.
- Only one-to-one conversations can be merged, and not while one is open in its own window or has unsaved text.

## 11. Search {#search}

### The search box {#search-box}

- **Search (Ctrl+E)** in the title bar suggests as you type: up to 4 **conversations**, 12 **messages** (with the words and who sent them) and 4 **contacts** you could start a chat with, marked **New chat**.
- If message search takes longer, conversations appear first. Messages and contacts follow below them without moving the results already shown.
- Choosing a conversation or message opens it there; choosing a contact opens [New chat](#new-chats-and-groups) ready to go.
- A line at the bottom says what the search covered, or "Some accounts could not be searched". "More messages match" means another word would narrow it.

### The full search window {#search-window}

Open it with **Search messages…** (More options or Commands), <kbd>Ctrl+F</kbd> in a conversation, or **Search conversation** in its details.

- **Search in**: Conversations (name or network), Conversation participants, Messages in all conversations, or Messages in the current conversation.
- **Filters** for message searches: **Person**, **Service**, **Content** (everything, photos, videos, links, files, audio, calls or voicemail), and **From** / **Through** dates. With a filter set, leave the words empty to browse: all photos from last month, say.
- Words match the **start** of words, ignoring capitals and accents, and every word must match: "rest caf" finds "Café restaurant". There's no special syntax.
- Results show the service, sender, date and the matching words, with the selected message beside the list. **Open result** opens it at that message. 30 results at a time, up to 500.

### What search covers {#search-coverage}

- **Google Messages**: Message Link quietly downloads your older texts into an encrypted archive, on your Message Link Server if you use one, so they're searchable even offline.
- **Messenger**: all the history Message Link keeps for it.
- **Instagram** and **Google Voice**: what's been loaded, recent conversations Message Link keeps, and Voice's call records. A result older than the newest 1,000 messages in a conversation can't be jumped to.
- Opening a result never marks a conversation read.

## 12. New chats and groups {#new-chats-and-groups}

Press <kbd>Ctrl+N</kbd>, choose **New chat** above the list, in the tray menu or the jump list, or pick a contact in the search box. New chat opens on the right with a "Choose a recipient" row in the list.

- **Choose a service**, or **All services** to look everywhere at once, then type a **name, phone number or handle**. Before you type, Message Link shows **Frequent**, **Suggested** and **Recent** people.
- Each pick becomes a pill on the To line; click a pill or press <kbd>Backspace</kbd> to remove it. Add several to make a group; everyone must be on the same service.
- **Start chat** (at the end of the To line, beside **Cancel**) opens it. A conversation that already exists just opens, and nothing is sent.
- **Phone numbers go through one texting service.** With Google Messages and Google Voice both connected, **All services** shows each phone number once, on your service for new texts (Google Messages until you choose). Once a number is on the To line the button says where it goes, such as **Text with Google Messages**; its arrow switches this text to the other service, and that becomes your choice for new texts from then on. It's also in [Settings → Messaging](#settings-messaging).
- Google Messages and Google Voice share contacts: each one also finds the people the other knows, so Google Messages finds your whole Google Contacts list when Voice is connected.
- **Groups**: Google Messages up to 20 other people, Messenger up to 50, Instagram up to 32, each with an optional **Group name**. Messenger groups made here are ordinary, not end-to-end encrypted. Google Voice can't start groups.
- **Instagram can't create an empty conversation.** When there's no conversation yet, New chat asks for **the first message** and the button becomes **Send and open**: that message really is sent.
- A phone number typed for Google Messages or Google Voice works even if it isn't in your contacts.

> **Careful:** A group appears in every member's list the moment it's created, and Message Link can't delete it. A group is never created twice: if it's unclear whether it worked, check before trying again.

> **Safe:** Looking people up on Messenger and Instagram waits until you've typed three letters and paused, so the services don't mistake you for a bot.

## 13. Notifications {#notifications}

Message Link shows Windows notifications while it's running, including from the tray. Turn them on or off with **Message notifications** in [Settings → Notifications](#settings-notifications).

### What a notification offers {#notification-actions}

- The conversation's name (or "Sender · Group"), the message and the service.
- **Copy code**, when the message holds a one-time code and notifications show messages. The code is held in memory only, for ten minutes, and can be copied once. It opens nothing and marks nothing read.
- **Reply** with a text box, and **Mark read**, where the service allows them.
- **Remind me in 1 hour** and **Mute** (until you unmute).
- Click it to open the conversation, in its own window if it's popped out.
- The buttons work even when Message Link is closed, for a week. Reply never sends twice: it says "Reply accepted", "Reply not sent" or "Reply outcome unknown", and in the last case you check the conversation before writing again.

### When notifications appear {#when-notifications-appear}

- **Not while you're in Message Link.** While the main window is in front, nothing is posted.
- **Quietly while you're away.** With the PC locked, the screen saver on, or no input for 15 minutes, notifications go straight to Notification Center without a banner or sound. The same while notifications are paused.
- **Never** for muted, archived or spam conversations, services you switched off, your own messages, edits, or older messages caught up after reconnecting.
- A conversation you asked to **Remind me later** stays quiet, then gives one reminder at that time.
- **Remind me about unread messages** (off by default) repeats a notification every 15 minutes until the conversation is read, muted or snoozed, for up to a day, and stays quiet while you're using Message Link or away.

### Pausing {#pausing-notifications}

- **Pause notifications** for 1 hour or until 8:00 AM tomorrow from the tray menu, Commands, Settings → Notifications → Sounds and privacy, or the console's `/pause`. Paused notifications still arrive silently in Notification Center, and a pause lasts through a restart.
- **Resume** wherever you paused. The tray tooltip says "notifications paused".

### Sounds and privacy {#notification-options}

**Settings → Notifications → Sounds and privacy**:

- **Services**: each service, and each Google Voice number, on or off, with its sound: one of Message Link's own, such as Pulse (the default) or Dock; Text message, Mail, Reminder or Windows default; one of nine more Windows sounds such as Chimes or Ding, a WAV file of your own under 2 MB (Choose a file), or No sound. Text message, Mail, Reminder and Windows default follow your Windows sound scheme. During Do Not Disturb, or for a scheduled reminder, any other sound is replaced by Text message. Choosing a sound plays it, and the play button beside it plays it again. Turning a service off here mutes nothing on the service.
- **Show in notifications**: **Sender and message**, **Sender only** ("New message") or **No details**. **Show the sender's photo** is off by default.
- **Pause**, as above.

**Send test** on the same page shows a test notification whose buttons answer without contacting anyone. Windows' own settings (Do not disturb, Focus) can still hold banners back; Message Link can't change those.

## 14. Privacy {#privacy}

### Private reading {#private-reading}

**Settings → Messaging → Private reading** turns it on per service. While it's on, reading a conversation on that service sends **no read receipt** and your typing isn't shown. The conversation is marked read on this PC only, so the other person and your phone still see it unread. Other people's links there show **Load preview** instead of a preview, since fetching one tells the link's site the message was opened.

To tell them after all: **Send read receipt** in the conversation menu, **Mark read** on a notification, or `/read` in the console.

### What stays in Message Link {#what-stays-local}

- Pins, mutes, labels, reminders, unread marks, saved messages, quick replies, drafts, and the names and photos you give conversations live in Message Link, never on the services. Without a Message Link Server that means this PC only. With one, they're on the server and your other devices that use it share them.
- On this PC, drafts, saved history, sign-ins, the Google search archive and your GIPHY key are stored protected for your Windows user.
- Diagnostic logs hold steps, counts and states: no message text, contact details or sign-in information.
- Search results, saved messages and the gallery never mark anything read.

### What leaves this PC {#what-leaves}

- Message Link talks to each service directly, unless you set up your own [Message Link Server](#messages-server).
- **Link previews** (on by default) fetch the linked page's picture and description, which tells that site the message was opened. Opening a conversation fetches them by itself only for your own links, and for links from people you know while [private reading](#private-reading) is off; any other link waits for you to click **Load preview**. None are fetched in a Spam conversation. Turn them off in [Appearance](#settings-appearance).
- **GIF search** goes to GIPHY, only when you use it.
- **The update check** looks once a day for a newer version, carrying nothing about you. Turn it off in [About](#settings-about).
- **Backups** go to the folder you choose; one that OneDrive, Google Drive or Dropbox syncs keeps a copy online.

## 15. Living on your desktop {#living-on-your-desktop}

### The system tray {#tray}

- By default, **closing the window keeps Message Link running** in the tray, still notifying. Choose **Quit Message Link** in the tray menu to exit. Change it with **Keep running when the window closes** in [Startup and tray](#settings-startup).
- **Minimize to the system tray** (off by default) hides the taskbar button when you minimize.
- Click the tray icon to open Message Link. Its tooltip says "Message Link · 3 unread conversations".
- The tray menu: **Open Message Link**, up to five unread conversations (and "N more unread…"), **New chat**, **Open console**, **Pause notifications** (or Resume), **Settings** and **Quit Message Link**.
- Quitting with unsaved text asks "Keep your unsaved messages?"; Message Link won't quit in the middle of a send.
- On a PC that runs its own Message Link Server (see [This PC's own Message Link Server](#own-server)), the menu also has **Manage server**, and **Stop Message Link Server** after **Quit Message Link**. **Quit Message Link** closes Message Link only: the server keeps your accounts connected and keeps reaching your phone. **Stop Message Link Server** closes Message Link, then the server, once nothing is still sending.

### The taskbar {#taskbar}

- A badge shows how many conversations are unread (1 to 9, then 9+). Unread counts leave out muted, archived and spam conversations.
- Right-click the taskbar button for the jump list:
  - **Pinned**: up to nine pinned conversations, alphabetically (seven when the suggestions need the room).
  - **Suggested**: three more conversations you're likely to want now. They're the ones you message most, counting the last few days most and the time of day you usually write to them; conversations you open count a little too. Until Message Link has a few days of that to go on, your most recent conversations fill in. What you send and open is recorded on this PC only, encrypted, and never with a message's words.
  - **New chat**, **Search** and **Quit Message Link** (quits as the tray's **Quit** does).
  - Each conversation shows its contact photo, or its coloured initial when there's no photo. Turn **Photos in the taskbar menu** off in [Startup and tray](#settings-startup) to use the app's icon instead. Windows can only show these pictures from ordinary files, so while they're on, a copy of each photo shown sits in the app's data folder; it goes when its conversation leaves the list, and all of them go when you turn the option off, sign out of the account or uninstall.
- A small progress bar on the button, and a dot above the list, show a launch that's still loading.

### Start with Windows {#start-with-windows}

**Start with Windows** starts Message Link in the tray when you sign in, connecting and notifying in the background. Nothing is marked read until you open it. If Windows' Startup apps page turned it off, Settings says so with a link there.

### Global shortcut {#global-shortcut}

<kbd>Ctrl+Alt+Shift+M</kbd> shows Message Link from any app, and pressing it again hides it. Choose <kbd>Ctrl+Alt+M</kbd>, <kbd>Win+Alt+M</kbd> or Off in [Startup and tray](#settings-startup). If another app holds the keys, Settings says "Another app is using …".

### Windows and restarts {#windows-and-restarts}

- Message Link remembers its window size, whether it was maximized, the list's width and the compact list. A first run opens centred.
- [Conversations in their own window](#pop-out-windows) come back where you left them.
- When you sign out of Windows or restart, Message Link saves your drafts and closes within a few seconds, without asking. A send still in flight is reported as unknown afterwards, never resent.
- Connections come back by themselves after sleep or a dropped network, and a service whose connection stops after it had connected resumes from its saved sign-in.

## 16. The chat console {#the-chat-console}

For keyboard people: <kbd>Ctrl+`</kbd> opens the chat console, a terminal of its own showing every service's messages as one live scroll, with a prompt. Open it from the main window, a conversation window, Commands or the tray (**Open console**); pressing <kbd>Ctrl+`</kbd> in it returns you to the prompt.

> **Safe:** Only four things send: plain text you type, `/r`, `/reply` and `/w`. They go through the same careful path as the message box. A send is never chained, piped, aliased or repeated by `!!`, and the console never shows you as typing.

> **Real people see these:** `/react`, `/like`, `/unreact`, `/reply`, `/edit`, `/unsend` and `/delete` act on real conversations: a reaction, a reply or an edit is seen by everyone in the conversation, and a delete changes your account and can't be undone. Each runs only when you type it on a line of its own, never from a chain, a pipe, an alias or `!!`, and never twice by itself.

### The screen {#console-screen}

- **The scroll** opens like a fresh terminal: the last login, `/neofetch`, then new messages as they arrive, each with its time, its service (in its colour) and the message. It holds 2,000 lines.
- **The prompt** sits after the last line: `messages>`, or `messages:Lauren>` once you've chosen someone, their name in their service's colour. Commands you type stay in the scroll with what they printed, as in any terminal.
- **The status line** at the bottom shows each service as a coloured letter with a dot (● connected, ○ offline), unread conversations, sends in flight, who's typing, your target and the clock. The title bar shows the size in characters, like `120×38`.
- **Themes**: `/theme campbell`, `phosphor` (green screen), `amber`, `ice` or `mono`. The console stays dark whatever your Windows theme; high contrast uses your system colours. Service colours stay their own in every theme.

### Talking {#console-talking}

| Command | What it does |
|---|---|
| plain text | Sends to the person the prompt shows |
| `/r text` | Replies to whoever wrote last. The recipient is fixed the moment you type "/r ", so a new arrival can't steal your reply. `/r` alone makes them the target |
| `/w name text` | Messages someone (also `/msg`, `/query`). `/w name` alone makes them the target. Add `@google`, `@messenger`, `@instagram`, `@voice` or `@voice:label` to choose a service; quote names with spaces |
| `/to name` | Chooses who plain text goes to, without sending or opening; `/to off` clears it |
| `/open name` | Opens the conversation in Message Link (where reading it marks it read, as usual) |
| `/next` | Opens the newest conversation with unread messages |
| `/read name` | Marks a conversation read on the service without opening it |
| `/mute name`, `/unmute name` | Mutes in Message Link |
| `/reply m3 text` | Replies quoting a message, in that message's conversation; `/reply text` alone is `/r` |
| `/react m3 👍`, `/like m3`, `/unreact m3` | Reacts to a message (or by name: `/react m3 heart`), likes it with the service's own like (👍, or ❤️ on Instagram), or takes your reaction back |
| `/edit m3 text`, `/unsend m3` | Changes the words of a message you sent, or removes it for everyone, where the service can |

To send a line that starts with a slash, start it with `//`; one that starts with `!`, start it with `\!`.

**Commands without the slash.** While no one is selected (the prompt shows `messages>`), nothing you type could be sent, so a line that starts with a command runs it: `ls unread`, `msgs 3`, `man ls`. Commands that send (`/r`, `/reply`, `/w`) still need their slash. Once someone is selected, plain text goes to them as always.

**Text that looks like a command.** With someone selected, a short line such as `ls unread` or `delete 3` isn't sent straight away: it stays in the prompt and the console says what Enter will do. Press <kbd>Enter</kbd> again within 15 seconds to send it as typed, or type it with its slash to run it. A sentence like "Open the door when you get here" goes at once.

### Managing conversations {#console-managing}

| Command | What it does |
|---|---|
| `/archive name`, `/unarchive name` | Archives on the service for Google Messages, Messenger and Google Voice; Instagram and linked conversations are archived in Message Link, and the console says which |
| `/pin name`, `/unpin name` | Pins in Message Link |
| `/unread name` | Marks it unread in Message Link, as **Mark as unread** does; the service isn't told |
| `/delete name` | Deletes a conversation where its menu offers **Delete…** (in two steps, below) |
| `/label name Family`, `/unlabel name Family` | Puts one of your labels on it, or takes it off |
| `/snooze name 1h` | **Remind me later**: `30m`, `1h`, `tonight`, `tomorrow`, a time like `17:30`, or `off` |
| `/copy m3`, `/save m3`, `/remind m3 tonight` | Copies a message's words, keeps it in Saved Messages, or reminds you about it later, all in Message Link |

### Acting on a conversation or message {#console-acting}

A conversation is named as everywhere else: a name, a number from the last `/ls` or `/search`, or `.`. A message is named by:

- `m3`: message 3 of the last `/msgs`. `/msgs` lists a conversation's recent messages, numbered with the newest last, from what this PC already keeps; nothing is asked of the service and nothing is marked read.
- a `/search` result number, or `#4`.
- `^`: the newest message from the person the prompt shows; `^2` the one before it.

`/delete` and `/unsend` take two steps. `/delete 3` prints exactly what will happen, in the words of the app's own Delete, and nothing else; `/delete 3 yes`, typed within 60 seconds for the same thing, does it. A message is deleted as its menu's **Delete for me** does, or **Delete for everyone** where that is all its service offers. Where a service can't do something (Google Voice can't react, Instagram conversations can't be deleted from Message Link), nothing is sent and the console says so. A change whose outcome is unknown is reported, never tried again by itself.

Add `--help` (or `-h`) to any command, such as `/delete 3 --help`, to see its manual page without running it.

### Looking {#console-looking}

| Command | What it does |
|---|---|
| `/ls [view]` | Numbered list of conversations: all (or `*`), unread, pinned, muted, archived, a service or a name |
| `/msgs [name] [count]` | A conversation's recent messages, numbered `m1`, `m2` …, from what this PC keeps. `/msgs *` lists every conversation's, one line each: `/msgs * \| grep "dinner"` finds a word in any of them |
| `/search words` | Numbered search results with the words highlighted; add `@messenger` and so on to narrow it. It looks deeper than `/msgs`, through Messenger's full store and Google's archive |
| `/grep words` | Finds lines already in the scroll, with the same rules as `\| grep` |
| `/thread [view] [name]` | Inspects a conversation from what this PC holds: `info` (flags, what it can do, merged members), `members`, `media`, `stats` (who writes, at what hours, how quickly each side answers), `raw` (ids and fields) and `route` |
| `/route name` | Traces, hop by hop, how a message would be sent and why that service, without sending |
| `/history [hours]` | Shows earlier messages kept on this PC: the last day, or up to 168 hours |
| `/filter what`, `/watch name` | Shows only some services, or one person; `/filter all` or `/watch off` resets |
| `/events quiet\|normal\|all` | How much shows: messages only, plus edits, reactions and connections, plus read receipts and typing |

**Names everywhere** can be typed in part, quoted, given `@service`, a number from the last `/ls` or `/search`, or `.` for the conversation open in Message Link: `/to 3`, `/thread stats .`.

### Console apps {#console-apps}

These take over the console's screen until you press <kbd>q</kbd> or <kbd>Esc</kbd>; the scroll is waiting exactly as you left it.

- `/top`: a live monitor with five pages (<kbd>Tab</kbd> or <kbd>1</kbd>–<kbd>5</kbd>): **overview** (services, their connections' memory and processor use, messages per minute, sends in flight, recent activity), **workers**, **queues** (sends in flight and scheduled), **cache** (what Message Link holds in memory and on disk) and **perf**. `/workers`, `/queues`, `/cache` and `/perf` open it on that page; <kbd>p</kbd> pauses it.
- `/chats`: a full-screen conversation browser. <kbd>Up</kbd>/<kbd>Down</kbd> move, <kbd>Enter</kbd> opens, <kbd>t</kbd> makes it your target, <kbd>i</kbd> and <kbd>s</kbd> inspect it, <kbd>m</kbd> mutes, <kbd>/</kbd> filters as you type, <kbd>Tab</kbd> changes view, <kbd>1</kbd>–<kbd>4</kbd> choose a service. <kbd>r</kbd> marks read only after you answer <kbd>y</kbd>.
- `/logs [source]`: the diagnostic logs, live. <kbd>Tab</kbd> changes log, <kbd>f</kbd> follows, <kbd>/</kbd> greps, <kbd>g</kbd>/<kbd>G</kbd> jump to the top or end, <kbd>c</kbd> copies what's shown.
- `/matrix`: you'll see.

### The shell {#console-shell}

- `a ; b` runs both commands; `a && b` runs b only if a worked.
- `|` sends a command's output through filters: `grep` (capitals make it exact), `head`, `tail`, `sort`, `uniq`, `wc`, `nl`, `cowsay`, `copy` (to the clipboard) and `less` (a full-screen pager). For example `/ls unread | head 5` or `/thread stats . | copy`.
- `!!` runs your last command again, `!3` the third in `/commands`, `!word` the last one starting with that word. A message is never re-sent this way: it comes back to the prompt for you to send or not.
- `/alias u=/ls unread` makes `/u` a command; `/alias` lists them and `/unalias u` removes one. Aliases run commands, never sends or changes to a conversation or message, and are saved protected on this PC.
- `/commands [words]` lists what you've typed, 500 lines kept while Message Link runs and never written to disk.

### Everything else {#console-other}

- `/status`: service health, unread, sends and notifications. `/diag`: a checklist of this copy of Message Link and its services. `/whoami`, `/uptime`.
- `/mirror`: with a Message Link Server, how its copies of your conversations compared with each service at its last check (it checks every half hour and reads again whatever differs). `/mirror now` starts a check. It only reads: nothing is sent or marked read.
- `/pause [1h|morning]`, `/resume`: notifications.
- `/clear` (or <kbd>Ctrl+L</kbd>) clears the scroll; nothing is deleted. `/latest` jumps to the newest line and follows again.
- `/help` lists commands, `/help talk` (or manage, look, apps, system, fun) lists one group with what each does, `/help command` (or `command --help`) shows its manual page, and `/help word` searches them. `/guide [topic]` opens this guide and `/tour` starts the tour.
- `/theme`, `/banner`, `/neofetch`, `/fortune`, `/cowsay text`, `/date`, `/echo text`, and `/matrix`, for fun.

### Console keys {#console-keys}

| Keys | What it does |
|---|---|
| <kbd>Tab</kbd> / <kbd>Shift+Tab</kbd> | Complete commands, options, filters and names; the choices list under the prompt |
| <kbd>Right</kbd> or <kbd>End</kbd> | Take the grey suggestion of an earlier command |
| <kbd>Up</kbd> / <kbd>Down</kbd> | Earlier and later lines you typed |
| <kbd>Ctrl+R</kbd> | Search what you typed (reverse-i-search); <kbd>Enter</kbd> takes the match without running it |
| <kbd>Ctrl+C</kbd> | Copy the selected lines; with none selected, drop the line you're typing |
| <kbd>Ctrl+U</kbd> | Clear the line up to the cursor |
| <kbd>Esc</kbd> | Clear the line, then the target |
| <kbd>Ctrl+L</kbd> | Clear the scroll |
| <kbd>PgUp</kbd> / <kbd>PgDn</kbd>, <kbd>Ctrl+Home</kbd> / <kbd>Ctrl+End</kbd> | Scroll, or jump to the top or the newest line |
| <kbd>Ctrl+Shift+C</kbd> | Copy the selected lines, or the whole scroll |
| <kbd>Ctrl+Plus</kbd> / <kbd>Ctrl+Minus</kbd>, <kbd>Ctrl+0</kbd> | Text size, and back to normal |
| <kbd>F1</kbd> | This chapter |
| <kbd>Ctrl+W</kbd> | Close the console |

To copy from the scroll, drag across it: inside one line a drag picks words, and once it reaches another line it picks whole lines, scrolling when you drag past the top or bottom. Shift+click and Ctrl+click add lines too. Right-click for **Copy selected lines**, **Copy shown transcript** and **Select all lines**.

While you type a command, its syntax shows at the right of the prompt.

## 17. Settings {#settings}

Open Settings with <kbd>Ctrl+,</kbd>, the tray menu or Commands (which can open each page directly). It always opens on **Accounts**. In a narrow window it shows the list of pages first. **Back to conversations** returns to your chats.

Settings uses Windows' page entrance when you open it or choose a section: the outgoing content fades away and the new section enters from below. Turn off **Animation effects** in Windows Settings → Accessibility → Visual effects to show it immediately.

Back to conversations reverses the navigation: Settings moves down and the inbox fades back in, keeping your place.

### Accounts {#settings-accounts}

Each service's card has a row for each of its accounts, in the same way for every service: the account (the phone's number for Google Messages, the account name for Messenger and Instagram, the number for Google Voice), with a status beside it: **Connected**, **Signed in** (**Paired** for Google Messages) or **Not connected**. A service with no account says **No account**.

- **Google Messages**: **Pair** the first time, then **Renew sign-in** (which keeps the pairing on your phone) or **Pair again** if your phone dropped this PC. **Connect**, **Reconnect** or **Refresh** reconnects. There's no Sign out: unpair from your phone. A red line warns when Google Messages isn't the default SMS app on your phone.
- **Messenger**: **Connect**, **Reconnect**, **Refresh** and **Sign out**. Encrypted conversations turn on by themselves when you sign in, and the card mentions them only if something is wrong. While they're on but their connection isn't, it reads **Connecting**, **Not connected**, **Removed** (Facebook no longer lists this PC) or **Keys missing** (this PC's encrypted-conversation keys are gone; Message Link never adds a Messenger device by itself, so the button to turn encrypted conversations on comes back for you to press), with a line saying what to do. After **Sign out** it reads **Off**: the keys stay, so signing in again uses the same device.
- **Instagram**: Connect, Reconnect, Refresh and Sign out.
- **Google Voice**: **Add Google Voice account**, then a row per number with its status and its own **Sign out**.
- **Message Link Server** is the first card. Choosing it opens **Connections › Server**, which connects Message Link to a Message Link Server you run yourself; on a PC that already uses one, the card shows the server's address and how it stands. Each account's card opens that service on the server's page in your browser, where accounts are connected and removed. See [Using a Message Link Server](#messages-server).
- **Contacts** (on a PC that uses a Message Link Server) says which phone saves the people you add with **Add to contacts**, and into which Google account, or that none does yet: turn on **Save contacts from your PC** in Message Link's Settings on your phone.

Signing out deletes that service's saved sign-in on this PC; scheduled messages for it stay, unsent. With a Message Link Server it signs out on the server, so the service leaves every device that uses it, and Message Link asks first.

### Using a Message Link Server {#messages-server}

New Tailscale devices use names beginning with **messagelink-**. Devices set up before the rename keep their existing names and addresses.

On a phone, choose **I have a server**, then **Scan your server's code**. Open **Add a phone** on the server's page, scan its code, check the server shown and choose **Join**. Opening a link alone never joins. Use only a code from your own server's page. The code carries the server's port; if the phone asks for an update, update Message Link and scan a new code.

**Settings → Connections → Message Link Server**

A Message Link Server is a small program you run at home so Message Link stays connected all day. Before a server is set up, the screen asks for its address; once there is one, it shows the address and the way to the server's page.

- **Not set up.** Enter the server's **Address** (its name or address and its port, like `server.lan:8787`) and choose **Connect**. The server's page opens in your browser, where you approve this PC. While it waits, this PC shows a short code; approve only if the code on the page is the same. Once approved, the page shows a **finish code**: paste it here, or type it and choose **Finish** (within 5 minutes). It proves the approval came from the real server, so this PC keeps nothing until it matches. The request holds for 10 minutes, and **Cancel** stops asking. Asking works only from the server's home network. Or choose **Use a pairing code instead**, enter the server's **Fingerprint** and a **Pairing code** from its page, and choose **Connect**. Have the line from **Add a PC** on the server's page? Paste it into **Address**: the address, fingerprint and pairing code fill in by themselves, and **Connect** joins. A phone's join link pasted there works the same way. Either way Message Link checks that the server answers, then remembers it. Nothing moves yet. A new server takes requests only once its page has a password; until then a device uses the pairing code shown when the server was installed.
- **Connected.** A card shows the server, with **Manage server…**, which opens its page. **Forget this server** removes the saved details.

**If the server is gone for good**, set up a new server, restore its latest backup from the new server's page, and connect your accounts there. Signing in to Messenger again adds a new device, and the old one may stay in your Facebook device list for you to remove. If Message Link says a move didn't finish, it offers to finish it when it opens.

Accounts are connected and removed on the server's page, and nowhere else: on a PC that uses a server, **Settings → Connections** shows how each one stands, and choosing one takes you to it there. No device outranks another, whichever joined first: each reads and sends, and managing the server takes the page's password. How a sign-in on the server looks is in [Signing in on the server](#server-sign-in).

Moving, either way, asks first, then closes Message Link the way **Quit** in the tray does (drafts are saved, and you are asked about anything still sending), does the move, tells you how it went and opens Message Link again by itself. If you have scheduled messages waiting, or changes that haven't reached the server yet, the move stops and says so; nothing changes and Message Link opens as it was.

**Manage the server** on Connections › Server opens the server's page in your browser, which warns about the page's certificate the first time because the server makes its own. The list down the side of that page goes to each of its pages; **Log** there shows the server's log as it is written: scroll up to read and it pauses, and **Jump to newest** picks it up again. While Message Link runs on a server, a notice says when a device asks to join. The same screen changes the server's address at any time and, for a PC removed from the server, takes a new pairing code from the page and joins again.

The server also has a page of its own, for a browser on your home network: open `https://` and the server's address with port 8787. Its first visit asks for the setup code the server printed when it started, then sets the page's password, and explains who manages the server, then walks through your accounts, Tailscale, notifications, a phone, a PC and backups (each can be skipped; a checklist on the page keeps what is left). It shows the devices waiting to join (approving one asks for the password again and gives you a finish code for that device's person to type there), the server's version and its address and fingerprint, its accounts and devices (each device with what it runs, and under **Details** its version, model and the address it last connected from), what the server keeps on disk (**Storage**), removes an account or a device (each asks for the password again), renames a device, and has **Add a device**, for a phone or a PC. Its **Tailscale** card signs the server in to your Tailscale network, ticks off each setting it needs, and can do them for you with **Fix it for me**. Turning off **Requests to join** on that page stops requests to join, including those already waiting, until you turn it on again. The pause survives a server restart; paired devices and pairing codes still work. A device denied or abandoned must wait five minutes before asking again.

Removing an account takes it off the server: the server signs out of it and forgets what it kept, so every device that uses the server stops showing it until you connect it again. Nothing changes at the service itself or on your phone, and your saved messages stay. For Messenger, the server would join as a new device if you signed in there again. Google Messages is removed by unpairing the server on your phone (Google Messages → Device pairing). An account in an unfinished move can't be removed. Once **Move back to this PC…** finishes, **Remove** forgets the server's paused copy; the account and its saved messages on the PC stay as they are.

### Signing in on the server {#server-sign-in}

You can add separate accounts for Google Messages, Messenger, Google Voice and Instagram from **Accounts** on the server page. Each account has its own **Sign in again** and **Remove** actions. Sign in again with the same account; use **Add another account** for a different one. Google Messages accounts must identify different phones. Google's paired-device limit still applies.

Update Message Link on every PC and phone before adding another account. Existing accounts keep working with older apps until an additional account is added. After that, older apps must be updated to reconnect, even if the added account is later removed. The account names in New chat and the service filters show which account you are using.

On a Windows server, open its page on that PC. The page opens a separate native sign-in window there; closing it cancels the attempt. Return to the page to start another attempt. Moving accounts between a PC and a server is unavailable once additional accounts have been added; the old move format cannot carry them all.

A Message Link Server signs your accounts in in its own browser, so the sign-in belongs to the server rather than to any PC. **Connect** on the Accounts card of the server's page opens that browser on the page. You sign in on the service's own page as you would anywhere.

- **Google Messages and Google Voice** finish by themselves once you're signed in; the window can stay up to half a minute longer while the server notices.
- **Messenger and Instagram** wait for you: choose **Done** once the service has finished with you (it may still ask whether to trust the browser, or for a code). The server then closes its browser, checks the sign-in and connects the account; Messenger turns on encrypted conversations by itself, as on this PC. If you choose Done too early, Settings says the sign-in didn't finish: choose **Connect** to start again.
- **Cancel**, or closing the window, stops the sign-in, and the server keeps nothing of it. Each Messenger or Instagram sign-in uses a fresh, empty browser on the server that is deleted afterwards. Instagram's inbox stays blocked in it, so nothing is marked read.
- A Windows server has no browser of its own: **Connect** on its page, in a browser on that PC, opens Message Link's own sign-in window there. A Linux server needs Google Chrome and a network of its own, and its page says so if it lacks them.

### This PC's own Message Link Server {#own-server}

A Windows PC can be its own Message Link Server: the server runs as you, starts when you sign in to Windows, and Message Link is its client like your phone is. It keeps Google signed in and your accounts connected even while the Message Link window is closed.

- **Switching.** Once a version of Message Link turns this on, a PC that runs Message Link on its own switches by itself the next time Message Link opens: it first copies what the server will use into the `Backups` folder of the Message Link data folder, then moves your choices onto the server, which signs in with the same accounts already on this PC (nothing to sign in again). If anything is scheduled or still sending, it waits for a later launch. If it can't switch, Message Link runs as before and says so once.
- **Starting.** The server starts when you sign in to Windows (it appears as "Message Link Server" in Task Manager's Startup apps, where you can turn it off). **Start with Windows** still starts Message Link itself in the tray, which is what shows your notifications.
- **Quitting.** **Quit Message Link** in the tray closes Message Link only. **Stop Message Link Server** closes both.
- **Its page.** **Manage server** in the tray, or in **Settings → Connections → Message Link Server** (which says this PC runs its own server, at `127.0.0.1`), opens the server's page in your browser, already signed in. Your browser warns that the page's certificate isn't one it knows, because the server makes its own; it's on this PC only. The page is where phones join (approve requests and remove devices there), where Tailscale is turned on or off, and where notifications and backups are set up. **Connect** on its Accounts card opens the service's own sign-in in Message Link.
- **Backups.** The server backs up once a day into the folder you choose in the page's **Backups** card: any folder on this PC, a mapped drive, a network share like `\\nas\backups`, or a OneDrive folder. A PC that already backed up keeps its folder and password.
- **Your phone** reaches the server through Tailscale once the server's door on your Tailscale network is turned on.

### Notifications {#settings-notifications}

| Setting | What it does | Default |
|---|---|---|
| **Message notifications** | Windows notifications with quick reply. Shows snoozes and a pause with **Resume** | On |
| **Remind me about unread messages** | Repeats every 15 minutes until read, muted or snoozed | Off |
| **Sounds and privacy** | Which services notify, their sounds, how much a notification shows, and Pause. See [Sounds and privacy](#notification-options) | |
| **Test notification** | **Send test**: a notification whose buttons contact no one | |

### Messaging {#settings-messaging}

| Setting | What it does | Default |
|---|---|---|
| **Undo send** | Off, 5 seconds or 10 seconds to take a message back. See [Undo send](#undo-send) | Off |
| **New texts** | Which service New chat uses for phone numbers when it searches all services. Shown when Google Messages and Google Voice are both connected. See [New chats](#new-chats-and-groups) | Google Messages |
| **Private reading** | Per service: read without sending read receipts or typing. See [Private reading](#private-reading) | Off |
| **GIF search** | Saves your free GIPHY key, which turns on the GIF tab, and removes it again | Off |

### Appearance {#settings-appearance}

| Setting | What it does | Default |
|---|---|---|
| **Theme** | **Same as Windows**, **Light** or **Dark**. A change applies when Message Link restarts; **Restart now** appears beside it | Same as Windows |
| **Message colours** | Your bubble colour per service and route: Google Messages RCS, SMS and MMS, and route unknown; Messenger; Instagram; Google Voice. Twelve swatches or **Custom colour**; a colour that would be hard to read is adjusted slightly. **Use service colours in linked conversations**, and **Reset to defaults** | Service colours |
| **Background** | Shown in dark mode only. The background colour in dark mode: **Dark**, **Gray**, **Near black**, or a tinted **Slate**, **Navy**, **Midnight** (the darkest blue), **Graphite** or **Moss**. The conversation list, the conversation and incoming bubbles change together, at once | Dark |
| **Service icons** | The service badge on conversation photos (<kbd>Ctrl+Shift+I</kbd>) | On |
| **Link previews** | A picture and description for web links | On |
| **Delivery status as text** | Sent, Delivered and Read in words instead of icons | On |

### Startup and tray {#settings-startup}

| Setting | What it does | Default |
|---|---|---|
| **Keep running when the window closes** | Stay in the tray, receiving messages and notifications | On |
| **Minimize to the system tray** | Hide the taskbar button when minimized | Off |
| **Photos in the taskbar menu** | Contact photos beside conversations in the [jump list](#taskbar) | On |
| **Start with Windows** | Start in the tray when you sign in to Windows | Off (offered once) |
| **Global keyboard shortcut** | Show Message Link from any app: Off, Ctrl+Alt+M, Win+Alt+M or Ctrl+Alt+Shift+M | Ctrl+Alt+Shift+M |

### Storage and backup {#settings-storage}

| Setting | What it does | Default |
|---|---|---|
| **Backup and restore** | Opens Backup. See [Backup](#backup-and-restore) | Off |
| **Storage** | Shows disk use and clears cached pictures. See [Storage](#storage) | |
| **Saved drafts** | How many conversations have unsent text, and **Clear drafts…** | |

### Troubleshooting {#settings-troubleshooting}

| Setting | What it does | Default |
|---|---|---|
| **Diagnostic logs** | **View logs**. See [Troubleshooting](#troubleshooting) | |
| **Connection health** | How each service stands, and a way to restart its connection. See [Troubleshooting](#troubleshooting) | |
| **Record diagnostics** | Save troubleshooting information while Message Link runs | On |
| **Reset Google Messages local data** | **Reset…**, shown only when Google sends have stopped because your phone renumbered its conversations | |

### About {#settings-about}

- The version, **Help** (**Take the tour** and **Open guide**), and **Updates** with **Check now**.
- **Check for updates automatically** (on): once a day Message Link looks for a newer version and tells you; **Download** opens the download page in your browser. Nothing installs by itself.

## 18. Backup, export and your data {#backup}

### Backup {#backup-and-restore}

**Settings → Storage and backup → Backup and restore**

- A backup is an encrypted copy of what only this PC keeps: Messenger's history, drafts, pins, labels, merges, names and photos you set, saved messages and their reminders, quick replies and preferences. Photos and videos in conversations aren't included.
- **Set password** (at least 8 characters). If you lose it, those backups can't be restored. **Change password** applies to new backups only.
- **Choose folder…**, or one of the synced folders Message Link finds (OneDrive, Google Drive, Dropbox), so a copy lives off this PC.
- **Back up every day** while Message Link is running, or **Back up now**. Each PC keeps its five newest backups, and PCs sharing a folder never remove each other's.
- **Check latest backup** reads the newest backup back. Message Link also checks it a day after writing it, then weekly.
- **Restore from backup…** asks for the file and its password, shows what's in it, then adds only what's missing. Messenger messages the backup knows were deleted are removed here too. Messenger history only goes back into the same Facebook account. Drafts, names and saved messages come back only to a PC that has none.

### Your Message Link Server's backup {#server-backup}

When Message Link uses a Message Link Server, the server keeps the backups and its page manages them. **Settings → Storage and backup → Backup and restore** appears only when they want a look: it shows how the backup stands (the last backup and its size, or why it failed), and choosing it opens the server's page there.

- Once a day the server writes one encrypted file to the location you choose on its **Backups** page. Choose a local folder or mapped drive, or enter a **Network share (SMB)** with its file server, share, optional subfolder, username and password. Create the subfolder first. A network share needs no Docker folder mapping; an existing mapped `/backups` folder still works. For a local Docker folder, use a mapped folder outside the server data and keys: folders inside the container are lost when it is replaced. The server checks that it can write, read, rename and delete a test file before keeping the choice.
- A backup holds your conversations, the server's own settings, its certificate and its devices, saved photos, scheduled messages, and Messenger's history. It never holds a service sign-in or the share's credentials: after a restore you sign in to each account again. The share's sign-in is kept protected on this server, separately from the password that encrypts your backups.
- The server keeps its five newest backups, and never removes the newest one that holds Messenger's history. It reads the newest back a day after writing it, then weekly.
- On the server's page, **Back up now** starts one and **Set a password** sets the password backups are sealed with (at least 8 characters, typed twice). If you lose it, those backups can't be read by anyone, the server included.
- If no backup has worked for two days, the server tells your phone.
- **Check now** reads the latest backup without restoring it. If no archive could be written, the server retries within an hour once a backup is due. If the share refuses your sign-in, backups pause until you enter its credentials again. An unavailable share does not mean the backup is damaged. A partial backup missing Messenger history is kept and daily backups continue, but it does not count as a new good backup.

**Restoring** is done on the server's page, and only onto a server with no account signed in: a new server offers **Restore from a backup…**, and **Reset server…** (type `RESET`) signs every account out and deletes everything an existing one holds first. Choose the backup and type its password; nothing is written if the password is wrong or the file is damaged. Confirm the server the backup came from is turned off, since two servers with one identity would split your devices. Then review the devices the backup brings back, each with when it last connected: untick any you removed or no longer have, or forget them all for all-new equipment. The server starts again and restores before it opens anything.

Choose the folder or network share holding the backup first. The review warns when Messenger history is missing. The server page's saved password returns to the one in the backup, so use that password after the restart. A password set in the server's installation settings stays as it is.

- Every device that's kept reconnects by itself, starts its copy of your conversations over and keeps its drafts. A device that joined after the backup was made is told **This server was restored from an earlier backup. Ask to join again.**
- Every scheduled message is held for you to send or drop, whether or not its time has come: the backup can't know which you cancelled or sent early since. Nothing that was sending is sent again.
- Sign in to each account again on the server; it then fills in what arrived meanwhile, and adds Messenger's history once Messenger is signed in.
- A backup made by a PC (before it used a server) restores onto a server too: its Messenger history and choices are added once those accounts are signed in on the server.
- If the server moved to new hardware at a new address, **Settings → Connections → Message Link Server** changes only the address and keeps this PC's pairing; the server's certificate proves it's the same server.

### Export a conversation {#export}

**Export conversation…** in the conversation menu saves what's loaded as a **web page** (.html) or **text** (.txt). Scroll up first to include more. Scheduled messages aren't included, nothing is marked read, and the file isn't encrypted.

### Storage {#storage}

**Settings → Storage and backup → Storage** shows what Message Link keeps: settings, drafts and saved history; pictures and videos kept for quick display; the Google Messages search archive; Messenger's history (the only copy, never changed here); saved sign-ins; logs; and everything else.

- **Free up space** makes Message Link's saved data take less room on disk.
- **Clear pictures…** removes cached pictures and videos; they download again when next shown, though some older ones may no longer be available from the service.
- **Saved photos** are the copies kept for saved messages. **Clear saved photos…** removes them without unsaving anything; Clear pictures never touches them.

A PC that uses a Message Link Server lists only what is on the PC, and **See what your server keeps** opens the server's page, which shows the rest.

### Reset Google Messages local data {#google-reset}

If your phone renumbers its conversations, Message Link stops Google sends and says so, and this appears in **Settings → Troubleshooting**. It clears what Message Link keeps for Google Messages (pins, labels, drafts, reminders, saved pictures and the search copy), on every device if you use a Message Link Server, and takes Google conversations out of merges. Nothing in Google Messages on your phone is deleted, and names and photos you set for people are kept.

### Where your data lives {#data-folder}

Everything is in `%LOCALAPPDATA%\Message Link` on your PC. Uninstalling asks whether to remove your sign-ins and saved conversations too; the answer defaults to **No**, so a reinstall picks up where you were.

## 19. Troubleshooting {#troubleshooting}

### Tools {#troubleshooting-tools}

- **View logs** (Settings → Troubleshooting) opens **Diagnostic logs**: choose a log from **Log source** (Message Link, each service, the run before this one, and Google Messages' sign-in history; on a PC that uses a Message Link Server, the server's logs too). **Following** keeps it live; **Copy**, and **Save all logs…** into one file to send to whoever helps you. Logs hold no message text, contact details or sign-in information. The console's `/logs` shows the same logs.
- **Connection health** (also in Commands) shows each service's state, its last activity and last message sent, and how long its connection has been running. **Restart** reconnects from the saved sign-in; it waits for any message still sending and asks before leaving one's outcome unknown. It never pairs again or signs in afresh.
- **Send test** (Settings → Notifications) checks notifications without contacting anyone.
- The console's `/diag` and `/status` summarize everything at once.

### Common problems {#common-problems}

- **"Google stopped accepting this computer's sign-in"**: Settings → Connections → **Renew sign-in**, with the same Google account. Message Link normally keeps it renewed by itself; your phone keeps the pairing.
- **"Google Messages is no longer paired" / Pair again**: if **Message Link** (or, if paired before the name changed, **Messages for Windows**) is still under Device pairing on your phone, choose **Renew sign-in**; if it's gone, **Pair as a new device**.
- **Google sends fail with "couldn't reach your phone"**: get your phone online, with Google Messages able to run.
- **"Not the default SMS app"**: make Google Messages the default SMS app on your phone.
- **A message says "May not have sent"**: check the conversation on another device before sending again. Message Link never resends by itself.
- **Facebook wants to confirm it's you**: finish its steps at facebook.com, then **Connect**.
- **"Facebook has signed Message Link out"** or **"Instagram has signed Message Link out"**: **Sign out**, then **Connect**.
- **"Instagram requires an account check"**: complete it in the Instagram app, then **Sign out** and **Connect** to sign in again. Message Link does not reconnect a session Instagram has flagged by itself, even after a restart.
- **Instagram messages arrive late**: by design, Instagram is checked every few seconds — up to about 20 seconds, a minute while Message Link is hidden or the PC is locked, and up to ten minutes for older conversations.
- **An Instagram conversation stays unread**: it's a spam or request conversation, which Message Link never marks read so the sender isn't told.
- **An Instagram message request is missing**: requests aren't shown; open Instagram to see and accept them.
- **A Messenger encrypted chat can't send**: wait for Messenger to reconnect. If Settings → Connections says encrypted conversations aren't on, sign in to Messenger again on the server's page.
- **Older Messenger encrypted messages are missing**: encrypted messages from before this PC was registered stay in Messenger's own backup and can't come here.
- **A Messenger message says "Couldn't read this message"**: the message couldn't be decrypted here, and Messenger won't deliver it again. Open Messenger on your phone to read it.
- **No notifications**: Message Link must be running (keep it in the tray, or start it with Windows). Check Settings → Notifications, the service's switch in Sounds and privacy, the conversation's mute, and whether notifications are paused. Then try **Send test**.
- **An option is missing from a menu**: that service doesn't offer it; see [What each service can do](#services).
- **Pins or mutes didn't show in a service's own app**: they stay in Message Link by design. Without a Message Link Server, they're on this PC only and don't reach Message Link on your phone either.
- **Windows warns about the installer**: the installer isn't signed, so Windows doesn't recognise it; choose **More info → Run anyway**.
- **"Message Link closed unexpectedly last time"**: choose **Save logs** and send the file to whoever helps you.

## 20. What each service can do {#services}

### At a glance {#capability-table}

| | Google Messages | Messenger | Google Voice | Instagram |
|---|---|---|---|---|
| Text | Yes | Yes | Yes | Yes (up to 8,192 characters) |
| Photos, GIFs, files | Yes; each photo is its own message | Yes, with albums | Photos only, up to 2 MB (larger ones are shrunk) | Photos; files and video up to 25 MB |
| Voice messages | Send and play | Send and play | No | Play only |
| Replies | RCS chats | Yes | No | Yes |
| Reactions | Yes | Yes (one each) | No | Yes (six emoji) |
| Edit | No | Yes | No | Your own text |
| Unsend | No (delete for me only) | Yes | No | Yes (for everyone only) |
| Read receipts | RCS only | Yes | No ("Sent") | Yes |
| "New messages" line | No | Yes | No | Yes |
| Typing | Yes | Yes | No | Yes |
| New groups | Up to 20 others | Up to 50, not encrypted | No | Up to 32 |
| Archive | On the service | On the service | On the service | This PC only |
| Delete a conversation | Yes | Yes | Yes | No |
| Calls and voicemail | No | No | View, play, save | No |
| Accounts | One phone | One | Many numbers | One |
| New messages arrive | At once (phone online) | At once | At once | Within about 20 seconds |

Everywhere: Message Link doesn't place or answer calls, and can rename a group or change its members only in Messenger's ordinary groups. Search can't jump to a result older than a conversation's newest 1,000 messages on Instagram or Google Voice, or in a Google Messages conversation that's still being indexed, though it still finds it. Pins, mutes, labels, unread marks, reminders and names you give conversations stay in Message Link and change nothing in the services or their own apps; devices that use the same server share them. A conversation muted in Messenger's own app shows muted here too, and unmuting it here leaves it muted there.

### Google Messages {#service-google-messages}

- **Needs** an Android phone with Google Messages, kept online, and Google Messages as its default SMS app.
- **Pairing**: **Connect** on the server's page, then Device pairing on the phone and the matching emoji. The phone shows the PC as **Message Link**; Messages for web keeps working beside it.
- **Sign-in**: Message Link keeps the Google sign-in renewed by itself. If that ever fails, **Renew sign-in** keeps the pairing.
- **Works**: texts, MMS and RCS, photos (each its own message), GIFs, files, voice messages, replies in RCS chats, reactions, delete for me, read receipts in RCS, typing, archive, delete a conversation, new chats and groups up to 20, encryption locks, link previews, contact photos, the sending SIM as a chip in the composer on dual-SIM phones, and a searchable archive of older texts.
- **Doesn't**: edit, unsend, a "New messages" line, choosing RCS or SMS (your phone decides), choosing a SIM (the chip only shows it), or managing groups.

### Messenger {#service-messenger}

- **Sign in** with Facebook on the server's page; Message Link reconnects by itself at every start. Encrypted conversations turn on after a new sign-in, registering this PC as a Messenger device.
- **Works**: ordinary and encrypted chats, photos, albums, videos, files and GIFs, voice messages, replies, reactions, edit, unsend, read receipts, typing, a "New messages" line, archive, delete a conversation (with its history on this PC), new chats, groups up to 50, renaming ordinary groups and adding or removing their members, and mutes set on your phone. History is kept on this PC, searchable, and included in backups.
- **Doesn't**: encrypted messages from before this PC was registered (they stay in Messenger's backup), encrypted groups (groups made here are ordinary, and encrypted ones can't be renamed or changed), removing someone from a group unless you're one of its admins, @mentions, Meta AI, or online status and last-active time. On encrypted chats a photo's caption goes as a separate message. Shared reels show a preview and a link.

### Google Voice {#service-google-voice}

- **Add** each number on the server's page and sign in at voice.google.com. Each number has its own filter, notification switch and sign-out, and reconnects by itself if it stops.
- **Works**: texts, photos up to 2 MB, read state both ways, archive, delete a conversation (from your Google Voice account), Not spam, call history, voicemail with transcripts (played and saved on demand), new chats to contacts or any number, and contact photos.
- **Doesn't**: voice messages, replies, reactions, edits, unsend, read receipts, groups, GIFs over 2 MB, marking spam, or placing calls. Spam stays out of every other view, count and notification.

### Instagram {#service-instagram}

- **Sign in** on the server's page with your Instagram username and password (not Facebook). Message Link reconnects at every start.
- **Works**: text, photos, files and video, replies, six reactions, editing your own text, unsend for everyone, read receipts, typing in inbox chats, a "New messages" line, received voice messages, GIFs and stickers, people search, new chats (sending a first message where none exists) and groups up to 32.
- **Doesn't**: message requests (not shown at all: open Instagram to see or accept one, since replying accepts it), archiving on Instagram (archive hides it on this PC), deleting a conversation, taking a chat out of spam, going to a quoted message that isn't loaded yet (scroll up to load it), playing shared reels (they show a preview and a link), recording voice messages, or showing an encryption lock. Spam and requests are never marked read.
- **Timing**: new messages take up to about 20 seconds, about a minute while Message Link is hidden or the PC is locked, and changes in older conversations up to ten minutes. Nothing that arrived while Message Link was closed is announced as a notification.
