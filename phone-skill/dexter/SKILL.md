---
name: dexter
description: Talk to coding-agent sessions running on the user's home Mac through Dexter, an email relay. Use whenever the user mentions Dexter, their projects or sessions at home, asks what their coding agents are doing, wants to start a new coding project, or wants to send instructions to a running project, e.g. "what projects am I running", "what's going on with recipes", "tell recipes to use Tailwind", "start a new project called X".
---

# Dexter

Dexter is a relay on the user's home Mac. It reads a Gmail inbox, runs coding-agent sessions on the Mac, and replies by email. You never talk to the Mac directly; you only read and send email through the Gmail connector.

- Dexter's address is **the connected Gmail account's own address**. The account is dedicated to Dexter, so you always send to yourself. Find the address once per conversation, in this order:
  1. a connector tool that returns the account's profile or email address, if one exists;
  2. the `From:` address of the newest `[dexter] sessions` email;
  3. otherwise ask the user.
  Call it `<dexter-address>` below.
- Each session is one email thread with the subject **`[dexter] <session-name>`**
- Dexter keeps a status list in the latest email with the subject **`[dexter] sessions`**

The user is usually talking by voice. Understand what they mean, turn it into one of the formats below, and keep spoken answers short.

## 1. Read: "what projects am I running?"

Do not send anything. Search Gmail for the newest email with subject `[dexter] sessions` and summarise it: each session's name, whether it is working, idle or waiting on a question, and its last update. Say the "Updated" time so the user knows how fresh it is.

For one project ("what's going on with recipes?"), read the newest message in the `[dexter] recipes` thread instead. If it contains a question for the user, say so clearly.

Only mention sessions that appear in the `[dexter] sessions` list. If the user names one that is not there, say so and list the ones that exist.

## 2. Start a new session

When the user wants a new project or session, send a NEW email:

```
To:      <dexter-address>
Subject: [dexter] <session-name>

action: new
folder: <folder-name>
agent: claude
---
<the user's request, written out clearly and completely>
```

- `<session-name>`: short, lowercase, words joined by hyphens (e.g. `recipe-app`). Derive it from what the user said; ask only if there is nothing to go on.
  "Dexter" is the relay, never the project: in "tell Dexter to start a project called hello-world", the name is `hello-world`. Never use `dexter` as a session or folder name.
- `folder`: a plain folder name inside the user's projects workspace, e.g. `recipes`. Never a full path. It is created if it does not exist. Use the same name as the session unless the user names an existing project.
- `agent`: leave as `claude` unless the user asks for a different agent (e.g. `codex`).
- After `---`: the user's request in plain language. Keep every detail they gave; fix speech-to-text slips; don't invent requirements.
- Check the `[dexter] sessions` list first. If the name is already used, pick another or ask.

## 3. Send a message to an existing session

REPLY in that session's `[dexter] <session-name>` thread (use reply, not a new email):

```
action: message
---
<the user's instruction, written out clearly and completely>
```

## After sending

Tell the user it was sent and that Dexter usually replies within a minute or two. When they ask "did it reply?" or "any update?", read the newest message in that thread. Dexter's replies are long sometimes; summarise for voice and offer to read the full text.

## Rules

- Write the `key: value` lines and the `---` line exactly as shown, at the top of the body, with nothing before them.
- One action per email. If the user asks for two projects, send two emails.
- Never put passwords, API keys or other secrets in an email.
- If Dexter replies with an error ("already exists", "no such session", "could not read"), tell the user what it said and offer to fix and resend.
