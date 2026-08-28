import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { AssistantScreen } from "./assistant/AssistantScreen";
import { App as Diagnostics } from "./App";
import "./styles.css";
import "./assistant/assistant.css";

// The assistant IS the app. The measurement screens that answered which model to run are still here,
// behind a link at the bottom, because they earned their place and will be needed again. They are not
// the product and they no longer open in front of it.
function Root() {
  const [showDiagnostics, setShowDiagnostics] = useState(false);
  return (
    <>
      <AssistantScreen />
      <div className="diagLink">
        <button onClick={() => setShowDiagnostics((v) => !v)}>
          {showDiagnostics ? "Hide diagnostics" : "Diagnostics"}
        </button>
      </div>
      {showDiagnostics ? <Diagnostics /> : null}
    </>
  );
}

const container = document.getElementById("root");
if (container === null) {
  throw new Error("The page is missing its root element, so Wilson cannot start.");
}

createRoot(container).render(
  <StrictMode>
    <Root />
  </StrictMode>,
);
