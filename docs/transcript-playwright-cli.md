# Playwright CLI for Claude Code - YouTube Transcript

**Source:** https://www.youtube.com/watch?v=I9kO6-yPkfM

---

Okay, Claude Code, I want you to spin up three parallel sub agents. I want you to use the playwright CLI skill for each of them and I want you to test my form submission inside of my website since we just made some changes. Go. So, what you're seeing happen right now is Claude Code just spawned three sub agents. All of them are using the Playwright CLI. Playwright is a tool from Microsoft, totally open source, that does browser automation. And this is a huge unlock for claude code because browser automation despite the fact that we have claude in Chrome as an extension is a place that cloud code really struggles.

And what you see here is one of the great use cases for browser automation. The idea that I can use it for UI testing. And so what we did in this example is I have my website. I made some adjustments to the form submission. That's what you see right here in the middle. And I'm now having Claude code test it from a number of different angles. Right? We're looking at edge cases, edge cases, validation, and also the happy path. And think about what this would look like normally. You would spin up the dev server on your own. You would manually go through it and you would manually test it, right? Hey, does it work when I put in a name, an email, whatever. But now I can test it from again a number of different angles simultaneously.

And via the Playwright CLI, we are doing this more effectively, more efficiently, and easier than really any other browser automation path. And again, I'm not touching anything. Cloud code via the playwright skill is doing all of this. And the use cases and potential upsides of knowing how to do something like this inside of Claude Code is frankly wild. Anything that requires you to interact with a browser is a place where we can use this. So in this video, I'm going to show you how the Playwright CLI works, how to install it, best practices, and most importantly, how to get the most out of it inside of the Claude Code ecosystem. This is going to be a huge productivity upgrade for you. So, let's get started.

But before we dive into Playwright, first a message from our sponsor, me. So, I just released a Claude code masterclass inside of Chase AI plus and I take you from zero to AI dev, no matter your technical background or lack thereof, in a practical manner. So, if you're looking to get way better at cloud code, definitely check it out. There's a link to it in the pin comments. Also, I have the free Chase AI community that is in the description. Tons of free resources. So if you're just looking for more stuff, definitely take a look.

Now back to Playwright. What is Playwright? Well, it's a framework for web testing and automation. It is a Microsoft product, but Microsoft was nice enough to open source this so we can use this for free. And what Playwright allows us to do is interact with our browsers programmatically, right? Exactly what you saw in the demo. That is the power of Playwright.

But hasn't Playwright been around for a while? Isn't there a Playwright MCP server? Why are we talking about Playwright CLI? And again, doesn't Claude already have something like this? Why are we talking about this tool in particular?

Well, the Playwright CLI command line interface is actually a relatively new addition to the Playwright Arsenal. This just came out a few weeks ago. Prior to that, we were in MCP land, and over the last month or so, you've probably seen Claude in Chrome extension.

## Why CLI is Better than MCP and Chrome Extension

Let's talk about why the CLI is so much better than these other two, and why this is such an important video. Now, while all three of these can interact with your browser in a programmatic way, only one of them does it in an efficient way, and that's the CLI.

Now, why is that? Dropping down to the bottom, it's because it has the lowest token usage by far. MCP is a token hog. In fact, there's a video that came out from Playwright themselves comparing the MCP to the CLI, and it was a difference of about 90,000 tokens for the same task. And we'll go into the why in a little bit.

Now, the Claude Code in Chrome extension that you've probably seen all over the place is also extremely token-heavy. Why is that? Because Claude in Chrome, the way it works is it takes screenshots of your web page. So, you know, when we're looking at that in here with my web page, it would actually take screenshots to figure out what's going on and then interact with it. That is very costly. Screenshots take up tons of tokens. And in fact, out of all these, the Claude Code extension is probably the worst because it is not headless and we cannot do it in parallel.

What do I mean by that? What does headless mean? Well, headless or headless browsers means that Playwright can operate a browser without it actually being open. So, you remember the demo how I had the website up. So, this is headed. This is a headed browser, meaning it's actually there. I can interact with it. Headless means it's working on the browser, but it's in the background. I can't see it. It's sort of invisible. We like the headless browsers because it's less of a drag on our machine. It's more efficient. I don't have 10 million things popping up on my desktop.

So the MCP can do headless as well, but so can the CLI. The other thing is, can we do this in parallel? Again, back to the demo. We had three CLI sub agents running tests. That's great. Can I do that with the Claude Code in Chrome browser? Not really, right? It's only going to be able to do it one tab at a time, and it's slow and expensive. The MCP can do this, and the CLI can do this.

Now looking at this chart, it should be pretty obvious why we're focused on the CLI then, right? I can do everything the MCP Playwright server can do and more at a significantly lower token usage.

## How the Accessibility Tree Works

So that's the why in terms of the tool. Now, in case you're wondering why there is such a massive token difference between the CLI and the MCP, kind of an interesting discussion. The way Playwright actually works, interesting enough, is it uses what's called an accessibility tree. So whenever you go on a website, there is what is called an accessibility tree and it's essentially mapping this entire website in a way that someone who couldn't see it could use it. Like imagine if you were blind, how would you interact with this website? Well, people figured that out because blind people need to use websites, too. That entire structure behind helping blind people use websites, the accessibility tree is the technology behind it. That's what Playwright actually uses to work.

But the Playwright MCP server, the way the MCP server works is it will take the entire accessibility tree and shove it into Claude Code. And the accessibility tree is actually relatively large. And so every time it shoves the accessibility tree into Claude Code, it's a massive token dump.

The CLI is a little bit different because while it still gets that accessibility tree, right, of all this information, it doesn't dump all of it into Claude Code. Instead, what it does is it takes that tree, saves it onto our computer on our disc. And all it does is give a summary of the tree to Claude Code. So it doesn't give it all the information. It instead just gives it the information that it needs. This is a way lower token cost. So that's why it's working in case you were wondering.

## Installation

So how do we get this installed and working with Claude Code? Actually very simple and easy. There's three things we're going to need:

1. We need to install the Playwright CLI
2. We need to install the browser engine
3. We need to set up the Claude Code skill for Playwright

So I'll give you the commands, but understand you can also just open up Claude Code, give it that GitHub repo, and say, "Hey, install everything I need."

But I'll show you the manual steps:

1. Install the CLI: `npm install -g @anthropic-ai/playwright-cli` (or similar)
2. Install browser engine: `npx playwright install chromium`
3. Install the skill: `playwright-cli install --skills`

Remember, reference the skills creator video. This is the skill that Microsoft came up with. And we can see the actual skill right here inside of the GitHub. You are not beholden to this. This will work just fine, but also know you can create it, edit it, use the skill creator to audit it. It's a living, breathing document.

## Usage and Best Practices

The amount of things that the Playwright CLI can actually do is pretty broad. It's an extremely powerful tool. So, I highly suggest one of the first things you do is just have an interaction with Claude Code asking it what can you do with the Playwright CLI skill and kind of going through some theoretical test cases that you think it can actually accomplish because ultimately your use cases will vary.

What we focused on here today is essentially what we saw in the demo, which is like this UI design type workflow or UI testing type workflow, which I think is something that's very common. But again, you could have this thing go like shop on Amazon for you. It has the ability to actually log in and set up persistent sessions and sort of have its own cookies type of deal. The waters here are very deep and we're just touching the surface, but again, Claude Code is your number one friend in understanding its capabilities.

But in terms of actual execution like with our UI testing, again we're using Claude Code. Claude Code uses the skill to execute the Playwright CLI on our behalf which means we just have to use plain language with what we want to do.

Some things to note: even though in the demo you saw all those tabs pop up, by default it is going to be headless which means when I tell it "hey go do this testing for me" you're not going to see that browser at all. So you need to be specific and actually say "hey I want it to be a headed browser I want to see it" or else you won't.

## Turning Workflows into Skills

If you actually want to supercharge this process, you need to learn how to package this sort of workflow into a skill itself. You just saw me use the Playwright CLI with plain language. Like, hey, run three parallel headed browser tests. Do I want to say this each and every time? Of course I don't. Yet, am I going to probably run this test on my local dev server to check the form every single time I make changes? I might be doing a ton of changes. I might need to run this test over and over and over.

So, what you need to be thinking about is how can I then turn that entire workflow into a skill? This entire process, this triple agent thing you saw in the demo, we can turn that into a skill and instead of having to describe the process every time, I can just say, "Hey, go do the Playwright CLI UI test skill. Go execute that skill." And it goes and does that.

And that's really easy to do actually:
1. First, you need to articulate the actual workflow that you've done
2. Next, use the brand new skill creator tool
3. Say: "I want to turn this workflow process into a skill" and paste the entire workflow

The skill creator will create the skill for you. Now you can just say "use the form tester skill" and three parallel agents will spawn just like in the demo. And because you used the skill creator, you now have the option to run tests and see if this is an actual improvement.

This is the sort of headspace you should be in with this stuff whenever you're doing workflows inside of Claude Code. Can we standardize it? And if it's standardized, can we turn that standard flow into a skill?

The waters are deep when it comes to Playwright. But with that complexity under the surface comes a huge swath of use cases. And luckily for us, Claude Code allows us to bridge that gap. We don't have to be crazy technical and in the weeds to really get a lot out of this because Claude Code abstracts so much of it away.
