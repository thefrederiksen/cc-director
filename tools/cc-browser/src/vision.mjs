// CC Browser - Anthropic API Vision Integration
// Direct fetch to Anthropic Messages API (no SDK dependency)

/**
 * Analyze a screenshot using Claude vision.
 * @param {string} base64 - Base64-encoded PNG image
 * @param {string} prompt - Text prompt describing what to analyze
 * @param {Object} [options]
 * @param {string} [options.model='claude-haiku-4-5-20251001'] - Model to use
 * @param {number} [options.maxTokens=1024] - Max tokens in response
 * @returns {Promise<string>} The model's text response
 */
export async function analyzeScreenshot(base64, prompt, { model = 'claude-haiku-4-5-20251001', maxTokens = 1024 } = {}) {
  const apiKey = process.env.ANTHROPIC_API_KEY;
  if (!apiKey) {
    throw new Error('ANTHROPIC_API_KEY env var required for CAPTCHA solving');
  }

  const response = await fetch('https://api.anthropic.com/v1/messages', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-api-key': apiKey,
      'anthropic-version': '2023-06-01',
    },
    body: JSON.stringify({
      model,
      max_tokens: maxTokens,
      messages: [{
        role: 'user',
        content: [
          { type: 'image', source: { type: 'base64', media_type: 'image/png', data: base64 } },
          { type: 'text', text: prompt },
        ],
      }],
    }),
  });

  if (!response.ok) {
    const errBody = await response.text();
    throw new Error(`Anthropic API error ${response.status}: ${errBody}`);
  }

  const result = await response.json();
  return result.content[0].text;
}
