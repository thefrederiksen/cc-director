/**
 * Security analyzer.
 * Checks: HTTPS, HSTS, CSP, X-Content-Type-Options, X-Frame-Options,
 *         Referrer-Policy, Permissions-Policy.
 */
export async function analyzeSecurity(crawlResult) {
  // Use headers from the homepage (first page)
  const homepage = crawlResult.pages[0];
  if (!homepage) {
    return {
      name: 'Security',
      weight: 0.10,
      checks: [{
        id: 'security-skip',
        name: 'Security Headers',
        status: 'SKIP',
        detail: 'No pages available to check',
        impact: 0,
        effort: 0,
      }],
    };
  }

  const headers = homepage.headers;
  const checks = [];

  checks.push(checkHsts(headers));
  checks.push(checkCsp(headers));
  checks.push(checkXContentType(headers));
  checks.push(checkXFrameOptions(headers));
  checks.push(checkReferrerPolicy(headers));
  checks.push(checkPermissionsPolicy(headers));

  return {
    name: 'Security',
    weight: 0.10,
    checks,
  };
}

function checkHsts(headers) {
  const hsts = headers['strict-transport-security'];

  if (!hsts) {
    return {
      id: 'hsts',
      name: 'HSTS',
      status: 'FAIL',
      detail: 'Strict-Transport-Security header not present',
      impact: 4,
      effort: 1,
    };
  }

  // Check max-age
  const maxAgeMatch = hsts.match(/max-age=(\d+)/);
  if (maxAgeMatch) {
    const maxAge = parseInt(maxAgeMatch[1]);
    if (maxAge < 31536000) {
      return {
        id: 'hsts',
        name: 'HSTS',
        status: 'WARN',
        detail: 'HSTS max-age is ' + maxAge + 's (recommended: 31536000 / 1 year)',
        impact: 4,
        effort: 1,
      };
    }
  }

  return {
    id: 'hsts',
    name: 'HSTS',
    status: 'PASS',
    detail: 'HSTS header present: ' + hsts,
    impact: 4,
    effort: 1,
  };
}

function checkCsp(headers) {
  const csp = headers['content-security-policy'];

  if (!csp) {
    return {
      id: 'csp',
      name: 'Content-Security-Policy',
      status: 'FAIL',
      detail: 'Content-Security-Policy header not present',
      impact: 4,
      effort: 3,
    };
  }

  // Check for unsafe-inline or unsafe-eval
  const issues = [];
  if (csp.includes("'unsafe-inline'")) issues.push("allows 'unsafe-inline'");
  if (csp.includes("'unsafe-eval'")) issues.push("allows 'unsafe-eval'");

  if (issues.length > 0) {
    return {
      id: 'csp',
      name: 'Content-Security-Policy',
      status: 'WARN',
      detail: 'CSP present but ' + issues.join(' and '),
      impact: 4,
      effort: 3,
    };
  }

  return {
    id: 'csp',
    name: 'Content-Security-Policy',
    status: 'PASS',
    detail: 'Content-Security-Policy header present and well-configured',
    impact: 4,
    effort: 3,
  };
}

function checkXContentType(headers) {
  const val = headers['x-content-type-options'];

  if (!val) {
    return {
      id: 'x-content-type-options',
      name: 'X-Content-Type-Options',
      status: 'FAIL',
      detail: 'X-Content-Type-Options header not present',
      impact: 3,
      effort: 1,
    };
  }

  if (val.toLowerCase().includes('nosniff')) {
    return {
      id: 'x-content-type-options',
      name: 'X-Content-Type-Options',
      status: 'PASS',
      detail: 'X-Content-Type-Options: nosniff is set',
      impact: 3,
      effort: 1,
    };
  }

  return {
    id: 'x-content-type-options',
    name: 'X-Content-Type-Options',
    status: 'WARN',
    detail: 'X-Content-Type-Options header present but not set to "nosniff": ' + val,
    impact: 3,
    effort: 1,
  };
}

function checkXFrameOptions(headers) {
  const val = headers['x-frame-options'];

  if (!val) {
    // Check if CSP frame-ancestors is set instead
    const csp = headers['content-security-policy'] || '';
    if (csp.includes('frame-ancestors')) {
      return {
        id: 'x-frame-options',
        name: 'X-Frame-Options',
        status: 'PASS',
        detail: 'Clickjacking protection via CSP frame-ancestors directive',
        impact: 3,
        effort: 1,
      };
    }

    return {
      id: 'x-frame-options',
      name: 'X-Frame-Options',
      status: 'FAIL',
      detail: 'X-Frame-Options header not present (no clickjacking protection)',
      impact: 3,
      effort: 1,
    };
  }

  const lower = val.toLowerCase();
  if (lower === 'deny' || lower === 'sameorigin') {
    return {
      id: 'x-frame-options',
      name: 'X-Frame-Options',
      status: 'PASS',
      detail: 'X-Frame-Options: ' + val,
      impact: 3,
      effort: 1,
    };
  }

  return {
    id: 'x-frame-options',
    name: 'X-Frame-Options',
    status: 'WARN',
    detail: 'X-Frame-Options has unexpected value: ' + val,
    impact: 3,
    effort: 1,
  };
}

function checkReferrerPolicy(headers) {
  const val = headers['referrer-policy'];

  if (!val) {
    return {
      id: 'referrer-policy',
      name: 'Referrer-Policy',
      status: 'WARN',
      detail: 'Referrer-Policy header not present (browser defaults apply)',
      impact: 2,
      effort: 1,
    };
  }

  const safe = [
    'no-referrer',
    'no-referrer-when-downgrade',
    'same-origin',
    'strict-origin',
    'strict-origin-when-cross-origin',
  ];

  if (safe.includes(val.toLowerCase())) {
    return {
      id: 'referrer-policy',
      name: 'Referrer-Policy',
      status: 'PASS',
      detail: 'Referrer-Policy: ' + val,
      impact: 2,
      effort: 1,
    };
  }

  return {
    id: 'referrer-policy',
    name: 'Referrer-Policy',
    status: 'WARN',
    detail: 'Referrer-Policy set to potentially leaky value: ' + val,
    impact: 2,
    effort: 1,
  };
}

function checkPermissionsPolicy(headers) {
  const val = headers['permissions-policy'];

  if (!val) {
    return {
      id: 'permissions-policy',
      name: 'Permissions-Policy',
      status: 'WARN',
      detail: 'Permissions-Policy header not present',
      impact: 2,
      effort: 1,
    };
  }

  // Check if sensitive features are restricted
  const restricted = [];
  const sensitive = ['camera', 'microphone', 'geolocation'];

  for (const feature of sensitive) {
    if (val.includes(feature + '=()') || val.includes(feature + '=self')) {
      restricted.push(feature);
    }
  }

  if (restricted.length >= 2) {
    return {
      id: 'permissions-policy',
      name: 'Permissions-Policy',
      status: 'PASS',
      detail: 'Permissions-Policy restricts: ' + restricted.join(', '),
      impact: 2,
      effort: 1,
    };
  }

  return {
    id: 'permissions-policy',
    name: 'Permissions-Policy',
    status: 'WARN',
    detail: 'Permissions-Policy present but does not restrict sensitive features',
    impact: 2,
    effort: 1,
  };
}
