import { useState } from "react";
import { Link } from "react-router-dom";
import { runWingmanAsk } from "@devthrottle/client-core/learning/learningClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The public GitHub repository link. github.com is a PUBLIC website (NOT a Director), so this external
// link does not violate Gateway-only-ingress; it is the same kind of intended absolute-URL exception
// the sign-in site carries, documented inline as the lint config requires.
// eslint-disable-next-line no-restricted-syntax -- documented Gateway-only-ingress exception (#967/#968): public docs link, not a Director
const GITHUB_URL = "https://github.com/devthrottle/devthrottle";

// The Learning / Help page (issue #977, epic #967) - the React port of the Blazor Cockpit
// Learning.razor(.css) (#472). Static curated content explaining what DevThrottle is and how to get
// started, plus an "Ask Wingman" box that answers free-text questions ABOUT THE PRODUCT. The answer is
// produced by the Gateway (POST /wingman/ask-devthrottle) - the Cockpit talks only to the Gateway,
// never a Director. When the ask cannot complete, the ask area shows an explicit message instead of
// failing silently (the no-fallback rule).
export function LearningView() {
  const [question, setQuestion] = useState("");
  const [askedQuestion, setAskedQuestion] = useState<string | null>(null);
  const [answer, setAnswer] = useState<string | null>(null);
  const [askError, setAskError] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);

  const ask = async () => {
    if (asking || question.trim().length === 0) return;
    const q = question.trim();
    setAskedQuestion(q);
    setAnswer(null);
    setAskError(null);
    setAsking(true); // immediate visual feedback: button + loading line flip before the call
    try {
      const outcome = await runWingmanAsk(q);
      // Exactly one of answer/error is set: this shows the answer or the error banner, never nothing.
      setAskError(outcome.error);
      setAnswer(outcome.answer);
    } catch (err) {
      // Defense in depth for the no-silent-failure rule (issue #1250): runWingmanAsk is built never to
      // throw, but if it ever did the page must still show a message rather than nothing at all.
      setAskError(gatewayErrorMessage(err));
    } finally {
      setAsking(false);
    }
  };

  return (
    <div className="page lrn">
      <div className="page-head">
        <h1>Learning</h1>
        <span className="page-sub">What DevThrottle is and how to use it.</span>
      </div>

      {/* ---- Overview ---- */}
      <section className="lrn-card">
        <h2>What is DevThrottle?</h2>
        <p>
          DevThrottle is mission control for your coding agents. It runs and supervises many Claude Code
          sessions at once, so you can keep a whole fleet of work moving instead of babysitting one
          terminal. It is open source - there is nothing hidden here.
        </p>
        <p>The product has three parts that work together:</p>
        <ul>
          <li>
            <strong>Director</strong> - the desktop app that runs and drives the coding sessions on each
            machine.
          </li>
          <li>
            <strong>Gateway</strong> - the service that gathers every machine's Directors into one fleet
            and serves the Cockpit.
          </li>
          <li>
            <strong>Cockpit</strong> - this web app, which the Gateway serves to every machine and
            to your phone.
          </li>
        </ul>
        <p>
          The <strong>Wingman</strong> is the built-in assistant that summarizes sessions and answers
          questions - including the Ask Wingman box on this page.
        </p>
      </section>

      {/* ---- Getting started ---- */}
      <section className="lrn-card">
        <h2>Getting started</h2>
        <ol>
          <li>
            Install the Director on each machine you want to run coding sessions on. The Gateway and
            Cockpit come with it.
          </li>
          <li>
            Open{" "}
            <Link className="lrn-link" to="/">
              Sessions
            </Link>{" "}
            to see your fleet, then start a new session and point it at a repository.
          </li>
          <li>
            Turn on Wingman in{" "}
            <Link className="lrn-link" to="/settings">
              Settings
            </Link>{" "}
            so it can summarize sessions and answer questions like the one below.
          </li>
          <li>
            Drive sessions from the Cockpit, your desktop, or your phone - the fleet is the same
            everywhere.
          </li>
        </ol>
      </section>

      {/* ---- Learn more ---- */}
      <section className="lrn-card">
        <h2>Learn more</h2>
        <p>
          The full source, documentation, and issue tracker live on GitHub:{" "}
          <a className="lrn-link" href={GITHUB_URL} target="_blank" rel="noopener noreferrer">
            github.com/devthrottle/devthrottle
          </a>
          .
        </p>
      </section>

      {/* ---- Ask Wingman ---- */}
      <section className="lrn-card lrn-ask">
        <h2>Ask Wingman</h2>
        <p className="lrn-ask-sub">
          Ask a question about DevThrottle - what it is, what it does, or how to use it.
        </p>

        <div className="lrn-askrow">
          <input
            className="lrn-input"
            type="text"
            placeholder="e.g. What is DevThrottle?"
            value={question}
            onChange={(e) => setQuestion(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") void ask();
            }}
            disabled={asking}
          />
          <button className="lrn-submit" onClick={() => void ask()} disabled={asking || question.trim().length === 0}>
            {asking ? "Asking..." : "Ask"}
          </button>
        </div>

        {asking ? (
          <p className="lrn-loading">Asking Wingman...</p>
        ) : askError !== null ? (
          <div className="lrn-askerror">{askError}</div>
        ) : answer !== null ? (
          <div className="lrn-answer">
            <div className="lrn-answer-q">You asked: {askedQuestion}</div>
            <div className="lrn-answer-a">{answer}</div>
          </div>
        ) : null}
      </section>
    </div>
  );
}
