/**
 * Structured Data analyzer.
 * Checks: JSON-LD presence, schema types, Organization schema, Article schema,
 *         FAQPage schema, BreadcrumbList, Person schema.
 */
export async function analyzeStructuredData(crawlResult) {
  // Extract all structured data across pages
  const allSchemas = [];
  const perPage = {};

  for (const page of crawlResult.pages) {
    const schemas = extractJsonLd(page.$);
    perPage[page.url] = schemas;
    allSchemas.push(...schemas);
  }

  const homepage = crawlResult.pages[0];
  const homepageSchemas = homepage ? (perPage[homepage.url] || []) : [];

  const checks = [];

  checks.push(checkJsonLdPresent(allSchemas, crawlResult.pages.length));
  checks.push(checkOrganizationSchema(homepageSchemas));
  checks.push(checkArticleSchema(perPage, crawlResult.pages));
  checks.push(checkFaqSchema(perPage));
  checks.push(checkBreadcrumbSchema(perPage));
  checks.push(checkSchemaValidity(allSchemas));

  return {
    name: 'Structured Data',
    weight: 0.10,
    checks,
  };
}

/**
 * Extract JSON-LD blocks from a page.
 */
function extractJsonLd($) {
  const schemas = [];

  $('script[type="application/ld+json"]').each((_, el) => {
    try {
      const text = $(el).html();
      if (!text) return;
      const parsed = JSON.parse(text);

      // Handle @graph arrays
      if (parsed['@graph'] && Array.isArray(parsed['@graph'])) {
        for (const item of parsed['@graph']) {
          schemas.push(item);
        }
      } else if (Array.isArray(parsed)) {
        schemas.push(...parsed);
      } else {
        schemas.push(parsed);
      }
    } catch {
      // Invalid JSON-LD
      schemas.push({ _parseError: true });
    }
  });

  return schemas;
}

function getType(schema) {
  const t = schema['@type'];
  if (Array.isArray(t)) return t;
  return t ? [t] : [];
}

function hasType(schemas, typeName) {
  return schemas.some(s => getType(s).includes(typeName));
}

function checkJsonLdPresent(allSchemas, pageCount) {
  if (allSchemas.length === 0) {
    return {
      id: 'json-ld-present',
      name: 'JSON-LD Structured Data',
      status: 'FAIL',
      detail: 'No JSON-LD structured data found on any page',
      impact: 5,
      effort: 2,
    };
  }

  const validSchemas = allSchemas.filter(s => !s._parseError);
  const errors = allSchemas.length - validSchemas.length;

  if (errors > 0) {
    return {
      id: 'json-ld-present',
      name: 'JSON-LD Structured Data',
      status: 'WARN',
      detail: allSchemas.length + ' JSON-LD block(s) found, but ' + errors + ' have parse errors',
      impact: 5,
      effort: 2,
    };
  }

  // List the schema types found
  const types = new Set();
  for (const s of validSchemas) {
    for (const t of getType(s)) types.add(t);
  }

  return {
    id: 'json-ld-present',
    name: 'JSON-LD Structured Data',
    status: 'PASS',
    detail: validSchemas.length + ' schema(s) found. Types: ' + Array.from(types).join(', '),
    impact: 5,
    effort: 2,
  };
}

function checkOrganizationSchema(homepageSchemas) {
  const org = homepageSchemas.find(s => {
    const types = getType(s);
    return types.includes('Organization') || types.includes('LocalBusiness') || types.includes('Corporation');
  });

  if (!org) {
    return {
      id: 'organization-schema',
      name: 'Organization Schema',
      status: 'FAIL',
      detail: 'No Organization/LocalBusiness schema on homepage',
      impact: 4,
      effort: 2,
    };
  }

  // Check for key properties
  const required = ['name', 'url'];
  const recommended = ['logo', 'description', 'sameAs', 'contactPoint'];
  const missing = required.filter(p => !org[p]);
  const missingRec = recommended.filter(p => !org[p]);

  if (missing.length > 0) {
    return {
      id: 'organization-schema',
      name: 'Organization Schema',
      status: 'WARN',
      detail: 'Organization schema present but missing required: ' + missing.join(', '),
      impact: 4,
      effort: 2,
    };
  }

  if (missingRec.length > 0) {
    return {
      id: 'organization-schema',
      name: 'Organization Schema',
      status: 'WARN',
      detail: 'Organization schema present but missing recommended: ' + missingRec.join(', '),
      impact: 4,
      effort: 2,
    };
  }

  return {
    id: 'organization-schema',
    name: 'Organization Schema',
    status: 'PASS',
    detail: 'Organization schema on homepage with complete properties',
    impact: 4,
    effort: 2,
  };
}

function checkArticleSchema(perPage, pages) {
  // Check if content pages (non-homepage) have Article/BlogPosting markup
  const contentPages = pages.filter(p => p.depth > 0);
  if (contentPages.length === 0) {
    return { id: 'article-schema', name: 'Article/BlogPosting Schema', status: 'SKIP', detail: 'No content pages to check (only homepage crawled)', impact: 3, effort: 2 };
  }

  let withArticle = 0;
  let missingProps = 0;

  for (const page of contentPages) {
    const schemas = perPage[page.url] || [];
    const article = schemas.find(s => {
      const types = getType(s);
      return types.includes('Article') || types.includes('BlogPosting') || types.includes('NewsArticle');
    });

    if (article) {
      withArticle++;
      // Check required properties
      if (!article.author || !article.datePublished) {
        missingProps++;
      }
    }
  }

  if (withArticle === 0) {
    return {
      id: 'article-schema',
      name: 'Article/BlogPosting Schema',
      status: 'WARN',
      detail: 'No Article/BlogPosting schema found on ' + contentPages.length + ' content page(s)',
      impact: 3,
      effort: 2,
    };
  }

  if (missingProps > 0) {
    return {
      id: 'article-schema',
      name: 'Article/BlogPosting Schema',
      status: 'WARN',
      detail: withArticle + ' page(s) with Article schema, but ' + missingProps + ' missing author/datePublished',
      impact: 3,
      effort: 2,
    };
  }

  return {
    id: 'article-schema',
    name: 'Article/BlogPosting Schema',
    status: 'PASS',
    detail: withArticle + ' page(s) with complete Article schema',
    impact: 3,
    effort: 2,
  };
}

function checkFaqSchema(perPage) {
  let found = false;

  for (const schemas of Object.values(perPage)) {
    if (hasType(schemas, 'FAQPage')) {
      found = true;
      break;
    }
  }

  if (!found) {
    return {
      id: 'faq-schema',
      name: 'FAQPage Schema',
      status: 'WARN',
      detail: 'No FAQPage schema found (3.2x more likely to appear in AI Overviews)',
      impact: 5,
      effort: 1,
    };
  }

  return {
    id: 'faq-schema',
    name: 'FAQPage Schema',
    status: 'PASS',
    detail: 'FAQPage schema detected',
    impact: 5,
    effort: 1,
  };
}

function checkBreadcrumbSchema(perPage) {
  let withBreadcrumb = 0;
  let total = Object.keys(perPage).length;

  for (const schemas of Object.values(perPage)) {
    if (hasType(schemas, 'BreadcrumbList')) {
      withBreadcrumb++;
    }
  }

  if (withBreadcrumb === 0) {
    return {
      id: 'breadcrumb-schema',
      name: 'BreadcrumbList Schema',
      status: 'WARN',
      detail: 'No BreadcrumbList schema found on any page',
      impact: 2,
      effort: 1,
    };
  }

  return {
    id: 'breadcrumb-schema',
    name: 'BreadcrumbList Schema',
    status: 'PASS',
    detail: 'BreadcrumbList schema found on ' + withBreadcrumb + '/' + total + ' page(s)',
    impact: 2,
    effort: 1,
  };
}

function checkSchemaValidity(allSchemas) {
  const errors = allSchemas.filter(s => s._parseError);

  if (allSchemas.length === 0) {
    return { id: 'schema-validity', name: 'Schema Validity', status: 'SKIP', detail: 'No schemas to validate', impact: 3, effort: 2 };
  }

  if (errors.length > 0) {
    return {
      id: 'schema-validity',
      name: 'Schema Validity',
      status: 'FAIL',
      detail: errors.length + ' JSON-LD block(s) have invalid JSON',
      impact: 3,
      effort: 2,
    };
  }

  // Check for missing @context
  const noContext = allSchemas.filter(s => !s['@context'] && !s._parseError);

  if (noContext.length > 0) {
    return {
      id: 'schema-validity',
      name: 'Schema Validity',
      status: 'WARN',
      detail: noContext.length + ' schema(s) missing @context property',
      impact: 3,
      effort: 2,
    };
  }

  return {
    id: 'schema-validity',
    name: 'Schema Validity',
    status: 'PASS',
    detail: 'All ' + allSchemas.length + ' schema block(s) have valid JSON and @context',
    impact: 3,
    effort: 2,
  };
}
