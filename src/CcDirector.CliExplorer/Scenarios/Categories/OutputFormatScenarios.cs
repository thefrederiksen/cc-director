namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class OutputFormatScenarios
{
    public static ScenarioCategory Create() => new(
        "Output Formats",
        "Different output format flags controlling response structure",
        new List<TestScenario>
        {
            new("OF-01", "--output-format text", "Default text output format",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format text",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-02", "--output-format json", "JSON output wrapping response in JSON",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-03", "--output-format stream-json", "Streaming JSON output with newline-delimited messages",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format stream-json",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-04", "stream-json + --include-partial-messages", "Includes partial token-by-token messages in stream",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format stream-json --include-partial-messages",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-05", "--input-format text", "Explicit text input format",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --input-format text",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-06", "--input-format stream-json", "Stream-json input format with JSON message on stdin",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --input-format stream-json",
                StdinText: "{\"type\":\"user_message\",\"content\":\"Say just the word pong\"}",
                CostsApiCredits: true),
        });
}
