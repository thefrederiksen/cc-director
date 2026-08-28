// Where a finished benchmark goes so nobody has to read numbers off a phone screen and repeat them.
//
// It writes the report to the function log and nothing else. No database, no storage account, no
// credentials: `vercel logs` is enough to read a result back, and a measurement rig does not deserve
// infrastructure. The receipt it returns is the timestamp, so the phone can show that the report
// actually arrived rather than assuming a request that was never read.

export default function handler(request, response) {
  if (request.method !== "POST") {
    response.status(405).json({ error: "Send the report with POST." });
    return;
  }

  const receivedAt = new Date().toISOString();
  let report = request.body;

  // Vercel parses JSON bodies, but only when the content type says so. A string here means it did
  // not, and parsing it is the difference between a readable log line and "[object Object]".
  if (typeof report === "string") {
    try {
      report = JSON.parse(report);
    } catch {
      response.status(400).json({ error: "The body was not JSON." });
      return;
    }
  }

  if (report === null || typeof report !== "object") {
    response.status(400).json({ error: "The body was not a benchmark report." });
    return;
  }

  // One line, whole report, so `vercel logs` shows it complete rather than in fragments that have to
  // be stitched back together.
  console.log("BENCHMARK " + JSON.stringify({ receivedAt, report }));

  const summary = Array.isArray(report.results)
    ? report.results.map((r) => `${r.modelId} ${r.device} ${r.status}`).join(", ")
    : "no results";
  console.log("BENCHMARK SUMMARY " + receivedAt + " :: " + summary);

  response.status(200).json({ receivedAt });
}
