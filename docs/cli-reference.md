# CC Director CLI Reference

Auto-generated from `--help` output. Use `<tool> <command> --help` for subcommand details.

---

## cc-browser

Browser automation with persistent workspaces.

```
USAGE: cc-browser <command> [options]

DAEMON:
  daemon              Start the daemon (keeps running)
  status              Check daemon and browser status

BROWSER LIFECYCLE:
  browsers            List available browsers
  profiles            List Chrome/Edge built-in profiles
  workspaces          List configured cc-browser workspaces
  favorites           Get favorites from workspace.json
  start               Start browser (--workspace, --browser, --incognito, --no-indicator)
  stop                Stop browser

NAVIGATION:
  navigate --url <url>    Go to URL
  reload                  Reload page
  back / forward          Navigate history

PAGE INSPECTION:
  snapshot [--interactive]     Get page structure with element refs
  info                         Page URL, title, viewport
  text [--selector <css>]      Page text content
  html [--selector <css>]      Page HTML

INTERACTIONS:
  click --ref <e1>             Click element by ref
  click --text "Label"         Click by text content
  click --selector ".btn"      Click by CSS selector
  type --ref <e1> --text "x"   Type into element
  press --key Enter            Press keyboard key
  hover --ref <e1>             Hover over element
  select --ref <e1> --value v  Select dropdown option
  scroll [--direction down]    Scroll viewport
  scroll --ref <e1>            Scroll element into view

SCREENSHOTS:
  screenshot [--fullPage]          Take screenshot (base64)
  screenshot --save ./page.png     Save to file
  screenshot-labels                Screenshot with element labels

TABS:
  tabs                     List all tabs
  tabs-open [--url <url>]  Open new tab
  tabs-close --tab <id>    Close tab
  tabs-focus --tab <id>    Focus tab

MODE:
  mode                     Show current mode
  mode human               Human mode (delays + mouse curves)
  mode fast                Fast mode (instant)
  mode stealth             Stealth mode (human + anti-detect)

RECORD & REPLAY:
  record start / stop --output file.json / status
  replay --file file.json [--mode fast]

CAPTCHA:
  captcha detect           Detect CAPTCHA on page
  captcha solve            Auto-solve CAPTCHA

SESSIONS:
  session create --name <n>    Create named session
  session list                 List active sessions
  session close --session <id> Close session tabs

JAVASCRIPT:
  evaluate --js "document.title"
  evaluate --js "el => el.textContent" --ref e1

ADVANCED:
  wait --text "loaded"         Wait for text
  wait --time 1000             Wait ms
  fill --fields '[...]'        Fill multiple form fields
  upload --ref <e1> --path f   Upload file

OPTIONS:
  --port <port>       Daemon port (default: 9280)
  --cdpPort <port>    Chrome CDP port (default: 9222)
  --browser <name>    Browser: chrome, edge, brave
  --workspace <name>  Named workspace (persists logins)
  --no-indicator      Hide automation info bar
  --tab <targetId>    Target specific tab
  --timeout <ms>      Action timeout
```

---

## cc-comm-queue

CLI for adding content to the Communication Manager approval queue.

```
USAGE: cc-comm-queue [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  add       Add content to pending_review queue
  add-json  Add content from JSON file or stdin
  list      List content items in the queue
  status    Show queue status and counts
  show      Show details of a specific item
  migrate   Migrate existing JSON files to SQLite
  config    Configuration management
```

### cc-comm-queue add

```
USAGE: cc-comm-queue add PLATFORM CONTENT_TYPE CONTENT

ARGUMENTS:
  PLATFORM      linkedin, twitter, reddit, youtube, email, blog
  CONTENT_TYPE  post, comment, reply, message, article, email
  CONTENT       The actual content text

OPTIONS:
  --persona        -p   Persona: mindzie, center_consulting, personal [default: personal]
  --destination    -d   Where to post (URL)
  --context-url    -c   What we're responding to (URL)
  --context-title       Title of content we're responding to
  --tags           -t   Comma-separated tags
  --notes          -n   Notes for reviewer
  --created-by          Agent/tool name
  --send-timing    -st  immediate, scheduled, asap, hold [default: asap]
  --scheduled-for       ISO datetime for scheduled send
  --send-from      -sf  Account: mindzie, personal, consulting
  --media          -m   Path to media file (repeatable)
  --json                Output as JSON (for agents)

  EMAIL-SPECIFIC:
  --email-to            Recipient email address
  --email-subject       Email subject line
  --email-attach        Attachment file path (repeatable)

  LINKEDIN-SPECIFIC:
  --linkedin-visibility  public, connections [default: public]

  REDDIT-SPECIFIC:
  --reddit-subreddit     Target subreddit
  --reddit-title         Reddit post title
```

### cc-comm-queue list

```
USAGE: cc-comm-queue list [OPTIONS]

OPTIONS:
  --status  -s  Filter: pending, approved, rejected, posted
            -n  Max results [default: 20]
```

---

## cc-crawl4ai

AI-ready web crawler: crawl pages to clean markdown.

```
USAGE: cc-crawl4ai [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  crawl    Crawl a single URL and extract content
  batch    Crawl multiple URLs in parallel
  session  Manage browser sessions
```

---

## cc-gmail

Gmail CLI: read, send, search, and manage emails.

```
USAGE: cc-gmail [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v
  --account  -a TEXT  Gmail account to use

COMMANDS:
  auth            Authenticate with Gmail
  list            List recent emails from a label/folder
  read            Read a specific email
  send            Send an email
  draft           Create a draft email
  reply           Create a draft reply
  drafts          List draft emails
  search          Search emails (Gmail query syntax)
  count           Count emails matching a query
  labels          List all labels/folders
  delete          Delete/trash an email
  untrash         Restore from trash
  archive         Archive email(s)
  archive-before  Archive all inbox before date
  profile         Show authenticated user profile
  stats           Mailbox statistics dashboard
  label-stats     Stats for a specific label
  label-create    Create a new label/folder
  move            Move email to a label
  accounts        Manage Gmail accounts
  calendar        Google Calendar operations
  contacts        Google Contacts operations
```

### cc-gmail list

```
USAGE: cc-gmail list [OPTIONS]

OPTIONS:
  --label   -l TEXT     Label/folder [default: INBOX]
  --count   -n INTEGER  Number of emails [default: 10]
  --unread  -u          Show only unread
  --include-spam        Include spam and trash
```

### cc-gmail send

```
USAGE: cc-gmail send [OPTIONS]

OPTIONS:
  --to       -t TEXT  Recipient email [required]
  --subject  -s TEXT  Email subject [required]
  --body     -b TEXT  Email body
  --file     -f PATH  Read body from file
  --cc          TEXT  CC recipients
  --bcc         TEXT  BCC recipients
  --html              Body is HTML
  --attach      PATH  Attachments
```

### cc-gmail search

```
USAGE: cc-gmail search [OPTIONS] QUERY

ARGUMENTS:
  QUERY  Gmail search query [required]

OPTIONS:
  --count   -n INTEGER  Number of results [default: 10]
  --include-spam        Include spam and trash
```

### cc-gmail read

```
USAGE: cc-gmail read [OPTIONS] MESSAGE_ID

OPTIONS:
  --raw  Show raw message data
```

---

## cc-hardware

Query system hardware information.

```
USAGE: cc-hardware [OPTIONS] COMMAND [ARGS]...

OPTIONS: --json -j, --version -v, --help

COMMANDS:
  ram      RAM information
  cpu      CPU information
  gpu      GPU information (NVIDIA only)
  disk     Disk information
  os       Operating system information
  network  Network interface information
  battery  Battery information
```

---

## cc-linkedin

LinkedIn CLI via browser automation.

```
USAGE: cc-linkedin [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --workspace  -w TEXT    cc-browser workspace
  --format        TEXT    Output: text, json, markdown [default: text]
  --delay         FLOAT   Delay between actions [default: 1.0]
  --verbose    -v         Verbose output

COMMANDS:
  status         Check daemon and LinkedIn login status
  whoami         Show logged-in LinkedIn user
  me             View your profile summary
  feed           View LinkedIn home feed
  create         Create a new post
  post           View a specific post
  like           Like a post
  comment        Comment on a post
  profile        View someone's profile
  connections    List connections
  connect        Send connection request
  messages       View recent messages
  message        Send a message
  search         Search LinkedIn
  notifications  View notifications
  invitations    View pending invitations
  accept         Accept invitation
  ignore         Ignore invitation
  repost         Repost content
  save           Save a post
  company        View company page
  jobs           Search for jobs
  goto           Navigate to a LinkedIn URL
  snapshot       Get current page snapshot
  screenshot     Take a screenshot
```

---

## cc-markdown

Convert Markdown to PDF, Word, or HTML with themes.

```
USAGE: cc-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input Markdown file [required]

OPTIONS:
  --output   -o PATH  Output file (.pdf, .docx, .html) [required]
  --theme    -t TEXT   Theme name [default: paper]
  --css         PATH   Custom CSS file
  --page-size   TEXT   Page size: a4, letter [default: a4]
  --margin      TEXT   Page margin [default: 1in]
  --version  -v        Show version
  --themes             List available themes
```

---

## cc-outlook

Outlook CLI: read, send, search emails and manage calendar.

```
USAGE: cc-outlook [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v
  --account  -a TEXT  Outlook account to use

COMMANDS:
  auth                 Authenticate (Device Code Flow)
  list                 List recent emails
  read                 Read a specific email
  send                 Send an email
  draft                Create a draft
  search               Search emails
  reply                Reply to an email
  forward              Forward an email
  flag                 Flag message for follow-up
  categorize           Set categories
  attachments          List attachments
  download-attachment  Download attachment
  delete               Delete/trash email
  archive              Archive (move to Archive folder)
  unarchive            Move from Archive to Inbox
  folders              List all mail folders
  profile              Show authenticated user
  accounts             Manage accounts
  calendar             Calendar operations
```

### cc-outlook list

```
USAGE: cc-outlook list [OPTIONS]

OPTIONS:
  --folder  -f TEXT     Folder: inbox, sent, drafts, deleted, junk [default: inbox]
  --count   -n INTEGER  Number of emails [default: 10]
  --unread  -u          Show only unread
```

### cc-outlook send

```
USAGE: cc-outlook send [OPTIONS]

OPTIONS:
  --to          -t TEXT  Recipient(s), comma-separated [required]
  --subject     -s TEXT  Subject [required]
  --body        -b TEXT  Body
  --file        -f PATH  Read body from file
  --cc             TEXT  CC recipients
  --bcc            TEXT  BCC recipients
  --html                 Body is HTML
  --attach      -a PATH  Attachments
  --importance  -i TEXT   low, normal, high [default: normal]
```

### cc-outlook search

```
USAGE: cc-outlook search [OPTIONS] QUERY

OPTIONS:
  --folder  -f TEXT     Folder to search [default: inbox]
  --count   -n INTEGER  Number of results [default: 10]
```

### cc-outlook read

```
USAGE: cc-outlook read [OPTIONS] MESSAGE_ID

OPTIONS:
  --raw  Show raw message data
```

---

## cc-photos

Photo organization: scan, categorize, detect duplicates, AI descriptions.

```
USAGE: cc-photos [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  discover  Discover where photos are located
  scan      Scan drives for photos
  dupes     Find and manage duplicates
  list      List images in database
  search    Search image descriptions
  analyze   Analyze images with AI
  stats     Database statistics
  source    Manage photo sources
  exclude   Manage excluded paths
```

---

## cc-powerpoint

Convert Markdown to PowerPoint presentations.

```
USAGE: cc-powerpoint [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Markdown file with --- slide separators [required]

OPTIONS:
  --output  -o PATH  Output .pptx file
  --theme   -t TEXT   Theme name [default: paper]
  --version -v        Show version
  --themes            List available themes
```

---

## cc-reddit

Reddit CLI via browser automation.

```
USAGE: cc-reddit [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --workspace  -w TEXT    cc-browser workspace
  --format        TEXT    Output: text, json, markdown [default: text]
  --delay         FLOAT   Delay between actions [default: 1.0]
  --verbose    -v         Verbose output

COMMANDS:
  status      Check daemon and Reddit login status
  whoami      Show logged-in Reddit username
  me          View your profile activity (--posts, --comments)
  saved       View saved posts and comments
  karma       Show karma breakdown
  goto        Navigate to a Reddit URL
  feed        View subreddit feed
  post        View a Reddit post
  comment     Add a comment to a post
  reply       Reply to a comment
  upvote      Upvote a post or comment
  downvote    Downvote a post or comment
  join        Join a subreddit
  leave       Leave a subreddit
  snapshot    Page snapshot (debugging)
  screenshot  Take a screenshot
```

---

## cc-spotify

Spotify CLI via browser automation.

```
USAGE: cc-spotify [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --workspace  -w TEXT   cc-browser workspace
  --format     -f TEXT   Output: text, json [default: text]
  --verbose    -v        Verbose output

COMMANDS:
  config     Configure cc-spotify settings
  status     Check daemon and Spotify status
  now        Show currently playing track
  play       Resume playback
  pause      Pause playback
  next       Skip to next track
  prev       Go to previous track
  shuffle    Toggle shuffle (--on/--off)
  repeat     Set repeat mode
  volume     Set volume (0-100)
  like       Heart/save current track
  search     Search tracks, artists, albums
  playlists  List library sidebar items
  playlist   Play a playlist by name
  queue      Show playback queue
  liked      List Liked Songs
  goto       Navigate to a Spotify URL
  recommend  Get music recommendations via vault
```

---

## cc-transcribe

Transcribe video/audio with timestamps and screenshots.

```
USAGE: cc-transcribe [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input video file (.mp4, .mkv, .avi, .mov) [required]

OPTIONS:
  --output       -o PATH   Output directory
  --screenshots             Extract screenshots at content changes [default: on]
  --no-screenshots          Disable screenshots
  --threshold    -t FLOAT   Sensitivity 0-1, lower=more [default: 0.92]
  --interval     -i FLOAT   Min seconds between screenshots [default: 1.0]
  --language     -l TEXT     Force language code (en, es, de)
  --info                     Show video info and exit
  --version      -v          Show version
```

---

## cc-vault

Personal Vault CLI: contacts, tasks, goals, ideas, documents.

```
USAGE: cc-vault [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  init            Initialize a new vault
  stats           Show vault statistics
  ask             Ask via RAG (--model, --no-hybrid)
  search          Semantic/hybrid search (-n, --hybrid)
  backup          Create full zip backup
  repair-vectors  Rebuild vector index
  restore         Restore from backup
  link            Create entity link
  unlink          Remove entity link
  links           Get links for entity
  context         Entity with linked context (for agents)
  tasks           Task management (list, add, done, cancel, show, update, search)
  goals           Goal tracking
  ideas           Idea capture
  contacts        Contact management (list, add, show, memory, update, search)
  docs            Document management (list, add, show, search, reindex)
  config          Configuration
  health          Health data
  posts           Social media posts
  lists           Contact list management
  graph           Graph statistics and traversal
```

### cc-vault ask

```
USAGE: cc-vault ask [OPTIONS] QUESTION

OPTIONS:
  --model  -m TEXT  OpenAI model [default: gpt-4o]
  --no-hybrid       Disable hybrid search
```

### cc-vault search

```
USAGE: cc-vault search [OPTIONS] QUERY

OPTIONS:
  -n INTEGER  Number of results [default: 10]
  --hybrid    Use hybrid search
```

---

## cc-video

Video utilities: info, extract audio, screenshots.

```
USAGE: cc-video [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  info         Show video information
  audio        Extract audio from video
  screenshots  Extract screenshots at content changes
  frame        Extract single frame at timestamp
```

---

## cc-voice

Convert text to speech.

```
USAGE: cc-voice [OPTIONS] TEXT

ARGUMENTS:
  TEXT  Text to convert (or path to text file) [required]

OPTIONS:
  --output  -o PATH   Output audio file (.mp3) [required]
  --voice   -v TEXT    alloy, echo, fable, nova, onyx, shimmer [default: onyx]
  --model   -m TEXT    tts-1, tts-1-hd [default: tts-1]
  --speed   -s FLOAT   0.25 to 4.0 [default: 1.0]
  --raw                Don't clean markdown formatting
  --version            Show version
```

---

## cc-whisper

Transcribe audio using OpenAI Whisper.

```
USAGE: cc-whisper [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  transcribe  Transcribe audio
  translate   Translate audio to English
```

---

## cc-youtube-info

Extract transcripts, metadata from YouTube videos.

```
USAGE: cc-youtube-info [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  info        Video metadata (title, channel, duration, stats)
  transcript  Download transcript
  languages   List available transcript languages
  chapters    List video chapters with timestamps
```

---

## Common Flag Patterns

Most tools use these consistent flags:

| Flag | Short | Meaning |
|------|-------|---------|
| `--count` | `-n` | Number of results (NOT `--limit`) |
| `--version` | `-v` | Show version |
| `--account` | `-a` | Account name (gmail, outlook) |
| `--output` | `-o` | Output file path |
| `--workspace` | `-w` | cc-browser workspace |
| `--format` | `-f` | Output format (text, json, markdown) |
| `--to` | `-t` | Recipient email |
| `--subject` | `-s` | Email subject |
| `--body` | `-b` | Email body |
| `--unread` | `-u` | Filter unread only |
| `--label` | `-l` | Gmail label/folder |
| `--folder` | `-f` | Outlook folder |
