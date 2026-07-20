# Trying the hosted DevThrottle

A short guide to running DevThrottle against the **cloud** gateway instead of the one on your own PC.

---

## The one thing to know first

**This does not change your normal DevThrottle.**

It is a **second, separate** DevThrottle. It has its own folder, its own settings and its own
sessions, and it runs alongside your usual one. Your everyday DevThrottle keeps running exactly as
it does now, with all your sessions, and nothing about it is touched.

This matters because your normal DevThrottle on this PC is the one running the whole agent fleet.
It must not be pointed at the cloud, because that would drag every running session with it. So
trying the cloud is done with a separate copy instead - which is what these three files are.

---

## Starting it

Double-click:

```
Start hosted DevThrottle.cmd
```

A DevThrottle window opens after a few seconds. It is **already signed in** to the cloud gateway -
there is nothing to log into and no code to paste.

If it is already running, it tells you so and does nothing.

## Stopping it

Double-click:

```
Stop hosted DevThrottle.cmd
```

This asks the program to close itself down tidily rather than killing it, so it does not leave a
stray "interrupted" entry behind. It waits and tells you when it has stopped.

Closing the window by hand also works, but the script is the cleaner way.

## Making sure you are really on the cloud

Double-click:

```
Show what the cloud sees.cmd
```

This is the honest check. It does **not** ask the program on your PC - it goes out to the cloud
gateway over the internet and prints whatever the cloud says back:

```
Asking the cloud gateway: https://devthrottle-gw.azurewebsites.net

DevThrottles the cloud can see: 1
   on SOREN_NORTH, version 1.5.0

Sessions the cloud can see: 2
   #100  my first hosted session   [Needs you]
   #101  another one               [Working]
```

If your sessions are listed there, they genuinely reached the cloud. If it says "none" right after
starting, wait about ten seconds and run it again.

**Your normal DevThrottle's sessions will never appear in this list.** That is the point: anything
you see here came from the cloud copy.

---

## What to expect

- Start it, open a session in it as you normally would, and that session runs on your PC as usual -
  but it is **reported to the cloud** rather than to your own gateway.
- Session numbers are handed out by the cloud, starting at 100.
- Colours, labels and "Needs you" states are decided by the cloud and shown to you.

---

## What does not work yet

None of these are things you are doing wrong. They are known, they have owners, and they are
listed here so nothing surprises you.

> **To whoever maintains this page: do not delete an item below because its fix merged.**
> Delete it when the fix is **on the deployed cloud gateway and you have seen the thing work**.
> Merged is not deployed - the cloud runs a container image, and merging to main does not change
> what is running on it. That distinction has already cost this project real time more than once,
> including a verification held back specifically because a merged fix was deliberately not yet on
> the box. An item removed early turns an honest warning into a lie, and the person reading this
> page has no way to tell.

**There is no cockpit and no phone view on the cloud gateway.** This is the big one. The cloud
image is deliberately built without the cockpit and mobile web apps, so `https://devthrottle-gw.azurewebsites.net`
in a browser will not give you a usable page. For now the **desktop window is the only way to see
and drive** the hosted sessions. Tracked as **issue #1892**, which is being treated as a go-live
gate rather than a bug - the reason the assets were excluded is out of date.

**Dictation does not work on the cloud gateway.** Deliberately left switched off, because turning
it on would have exposed a hole where one account could request another account's transcript.
Tracked as **issue #1884**.

**Cross-account separation is not something you can see from here.** You are the only account on
the cloud gateway at the moment, so there is nothing to compare against. It has been tested
separately with two accounts.

**The browser sign-in page will reject your key.** If you find `/login` on the cloud gateway and
paste a device key into it, it will say no. That is a quirk of that page, not a sign that anything
is wrong with your setup. Noted in **issue #1892**.

---

## If something looks wrong

1. Run `Show what the cloud sees.cmd`. It tells you whether the cloud can see your DevThrottle at
   all, which separates "not connected" from "connected but misbehaving".
2. If it says the cloud can see no DevThrottles while the window is open, the connection is the
   problem, not the sessions.
3. The log lives in `testroot\logs\director\` - newest file, one per run.

---

## For whoever maintains this

The engineering detail behind this setup - the isolated storage root, why it is a separate slot
build, how it was enrolled and how each leg was verified - is in
[hosted-real-director-test-rig.md](hosted-real-director-test-rig.md). Read that before changing
anything here, particularly the section on `CC_DIRECTOR_ROOT` and the storage migration.

Copies of the three scripts are kept in
[hosted-dogfood/](hosted-dogfood/) so they survive the machine they were written on. **They contain
absolute paths** for the setup they were built for, so check the paths inside before reusing them
elsewhere.
