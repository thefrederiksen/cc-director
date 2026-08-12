# Security Policy

Thank you for taking the time to report a security problem. This page tells you how to reach us privately, what we will do with your report, and what we consider in scope.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.** A public issue tells everyone how to attack DevThrottle users before there is a fix for them to install.

Report it privately, either way:

- **Preferred: GitHub's private reporting.** Go to the [Security tab](https://github.com/thefrederiksen/devthrottle/security) and choose **Report a vulnerability**. This opens a private conversation that only we can see. No email address needed.
- **By email:** email@devthrottle.com

Please include as much of this as you can:

- What the problem is, in one or two sentences.
- The steps to reproduce it, or a short proof-of-concept.
- The DevThrottle version (**Help -> About** in the app, or `devthrottle-setup-cli status`) and your operating system.
- Whether you were using the hosted gateway, a self-hosted gateway, or no gateway at all.
- What an attacker could do with it.

Write in English. Attachments are welcome.

## What happens next

| When | What we do |
|---|---|
| Within 3 business days | We acknowledge your report and tell you who is handling it. |
| Within 10 business days | We tell you whether we can reproduce it, whether we consider it a vulnerability, and how severe we think it is. |
| After that | We keep you updated at least every two weeks until it is fixed or closed. |

When a fix ships we name it in the release notes and credit you by whatever name or handle you ask for -- or leave you out entirely if you would rather not be named. Just tell us which.

We ask that you give us a reasonable chance to ship a fix before publishing details. Ninety days is the norm; if the problem is being actively exploited we will move much faster and will say so.

**There is no paid bug bounty.** We are a small team and we would rather be honest about that up front than have you find out after the work.

## Which versions get fixes

The **latest release** is the supported version. Security fixes go into the next release from `main`, and the desktop app updates itself, so staying current is the whole of the support policy. Older versions are not patched.

## What is in scope

- The Director desktop application, the Launcher, and the tray gateway app.
- The gateway -- both the one we host at devthrottle.com and a self-hosted one built from this repository.
- The web cockpit and the mobile web application.
- The installer and `devthrottle-setup-cli`.
- The `cc-*` command line tools that ship with the product.

Things we especially want to hear about: anything that lets one account reach another account's data on the hosted gateway; anything that lets an unauthenticated caller drive a gateway or a Director; a way to read the account key or session keys off a machine without already being that user; a way to get code to run through the installer or the self-update path.

## What is not a vulnerability

These are how the product is designed to work, so a report about them will be closed as intended behaviour:

- **DevThrottle runs coding agents on your machine with your permissions.** Those agents read and write your files and run commands. That is the entire purpose of the product, not a sandbox escape.
- **Credentials are stored in your own user profile** under `%LOCALAPPDATA%\cc-director`. Anyone who is already signed in as you, or who has administrator rights on your machine, can read them. Protecting your own logon session is your operating system's job.
- **The Director opens no inbound network port.** If you deliberately expose a self-hosted gateway to the public internet yourself, what reaches it is your configuration, not our defect.
- Missing security headers, cookie flags, or scanner output with no demonstrated impact.
- Reports produced entirely by an automated scanner with nothing reproduced by hand.

## Please do not

When testing, do not do anything that affects other people or the running service:

- No load testing, denial-of-service testing, or automated scanning against devthrottle.com or the hosted gateway. Test against a gateway you run yourself.
- Do not access, change, or keep data belonging to another account. If you reach someone else's data by accident, stop, and tell us what happened.
- No social engineering of anyone, and no physical attacks.

Stay inside those lines and we will treat your report as a good-faith contribution and will not pursue you for it.
