import type { SessionDto } from "../api/client";
import { classify, isWorking, machineCanBeActedOn } from "../sessions/ordering";
import { rowVoiceInputs } from "./clips";
import { isVoiceReady } from "./voiceRowState";
import { inVoiceQueueOrder } from "./queueTouch";

/**
 * THE voice queue: the sessions the hands-free lens will read out, in the order it will read them.
 *
 * WHY THIS EXISTS AS ONE FUNCTION. Inspection 1, finding 2 found the phone deciding for itself whether a
 * machine could be reached - it passed "does this row carry a retention mark?" as the `reachable`
 * argument, which is a question about how recently the machine PUSHED, not about whether it can be
 * reached. A machine whose tunnel was up but momentarily quiet therefore dropped out of the queue, and
 * the owner stopped being told about work he could have acted on immediately.
 *
 * The inspection also named why the tests did not catch it: they handed `isVoiceReady` hand-built
 * `reachable` values, so replacing the argument at the call site with a constant left them green. The
 * lesson is not "write a test for the helper" - it is that a view holding a substitutable boolean is
 * where the defect lives. So the whole decision moves here, the caller passes SESSIONS and nothing else,
 * and there is no argument left at the call site to get wrong.
 *
 * Reachability comes from the Gateway's own stamp via {@link machineCanBeActedOn}, per the rule that the
 * Gateway owns every verdict and a client only renders one.
 *
 * This is the tab's roster AND the row's triangle by construction: `isVoiceReady` is exactly
 * "voiceRowState === ready", so the queue and the triangle cannot disagree about what ready means.
 */
export function voiceQueueFor(sessions: SessionDto[]): SessionDto[] {
  return inVoiceQueueOrder(
    sessions.filter(
      (s) => classify(s) === "needsYou" && isVoiceReady(rowVoiceInputs(s, isWorking(s), machineCanBeActedOn(s))),
    ),
  );
}
