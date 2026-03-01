/**
 * Site Profile Analyzer
 *
 * Detects hosting provider and framework/CMS from HTTP headers,
 * HTML patterns, script/link URLs, and cookies. Returns informational
 * findings (not scored pass/fail).
 */

// ---------------------------------------------------------------------------
// Fingerprint registries
// ---------------------------------------------------------------------------

const HOSTING_HEADERS = [
  { header: 'x-vercel-id',         hosting: 'Vercel' },
  { header: 'x-vercel-cache',      hosting: 'Vercel' },
  { header: 'x-nf-request-id',     hosting: 'Netlify' },
  { header: 'x-amz-cf-id',         hosting: 'AWS CloudFront' },
  { header: 'x-amz-cf-pop',        hosting: 'AWS CloudFront' },
  { header: 'x-amzn-requestid',    hosting: 'AWS' },
  { header: 'x-shopify-stage',     hosting: 'Shopify' },
  { header: 'x-wix-request-id',    hosting: 'Wix' },
  { header: 'x-azure-ref',         hosting: 'Microsoft Azure' },
  { header: 'x-do-app-origin',     hosting: 'DigitalOcean' },
  { header: 'x-github-request-id', hosting: 'GitHub Pages' },
  { header: 'x-served-by',         hosting: 'Fastly', match: /cache-/i },
  { header: 'fly-request-id',      hosting: 'Fly.io' },
  { header: 'x-render-origin-server', hosting: 'Render' },
  { header: 'x-kinsta-cache',       hosting: 'Kinsta' },
  { header: 'ki-edge',              hosting: 'Kinsta' },
  { header: 'ki-cache-type',        hosting: 'Kinsta' },
  { header: 'ki-cf-cache-status',   hosting: 'Kinsta' },
  { header: 'ki-origin',            hosting: 'Kinsta' },
];

const HOSTING_SERVER_VALUES = [
  { match: /cloudflare/i,    hosting: 'Cloudflare' },
  { match: /netlify/i,       hosting: 'Netlify' },
  { match: /vercel/i,        hosting: 'Vercel' },
  { match: /github\.com/i,   hosting: 'GitHub Pages' },
  { match: /squarespace/i,   hosting: 'Squarespace' },
  { match: /dps/i,           hosting: 'GoDaddy' },
  { match: /nginx/i,         server: 'nginx' },
  { match: /apache/i,        server: 'Apache' },
  { match: /microsoft-iis/i, server: 'Microsoft IIS' },
  { match: /litespeed/i,     server: 'LiteSpeed' },
  { match: /caddy/i,         server: 'Caddy' },
];

const HOSTING_COOKIE_SIGNALS = [
  { cookie: '__cf_bm',       hosting: 'Cloudflare' },
  { cookie: '_shopify_s',    hosting: 'Shopify' },
  { cookie: '_shopify_y',    hosting: 'Shopify' },
  { cookie: 'crisp-client',  hosting: null }, // not hosting, ignore
];

const HOSTING_URL_PATTERNS = [
  { match: /cdn\.shopify\.com/i,       hosting: 'Shopify' },
  { match: /\.wixstatic\.com/i,        hosting: 'Wix' },
  { match: /cdn\.webflow\.com/i,       hosting: 'Webflow' },
  { match: /\.vercel\.app/i,           hosting: 'Vercel' },
  { match: /\.netlify\.app/i,          hosting: 'Netlify' },
  { match: /\.squarespace\.com/i,      hosting: 'Squarespace' },
  { match: /\.cloudfront\.net/i,       hosting: 'AWS CloudFront' },
  { match: /\.azureedge\.net/i,        hosting: 'Microsoft Azure CDN' },
  { match: /\.akamaized\.net/i,        hosting: 'Akamai' },
  { match: /\.googleapis\.com/i,       hosting: 'Google Cloud' },
  { match: /\.firebaseapp\.com/i,      hosting: 'Firebase' },
  { match: /\.github\.io/i,            hosting: 'GitHub Pages' },
];

const FRAMEWORK_META_GENERATOR = [
  { match: /wordpress/i,       framework: 'WordPress' },
  { match: /drupal/i,          framework: 'Drupal' },
  { match: /joomla/i,          framework: 'Joomla' },
  { match: /hugo/i,            framework: 'Hugo' },
  { match: /jekyll/i,          framework: 'Jekyll' },
  { match: /ghost/i,           framework: 'Ghost' },
  { match: /typo3/i,           framework: 'TYPO3' },
  { match: /gatsby/i,          framework: 'Gatsby' },
  { match: /webflow/i,         framework: 'Webflow' },
  { match: /wix\.com/i,        framework: 'Wix' },
  { match: /squarespace/i,     framework: 'Squarespace' },
  { match: /shopify/i,         framework: 'Shopify' },
  { match: /next\.js/i,        framework: 'Next.js' },
];

const FRAMEWORK_HTML_MARKERS = [
  // WordPress
  { pattern: /wp-content\//i,             framework: 'WordPress' },
  { pattern: /wp-includes\//i,            framework: 'WordPress' },
  // Next.js
  { pattern: /id="__next"/i,              framework: 'Next.js' },
  { pattern: /__NEXT_DATA__/,             framework: 'Next.js' },
  // Nuxt
  { pattern: /id="__nuxt"/i,              framework: 'Nuxt.js' },
  { pattern: /__NUXT__/,                  framework: 'Nuxt.js' },
  // React (generic SPA)
  { pattern: /id="root".*<\/div>\s*<script/i, framework: 'React' },
  { pattern: /react-root/i,               framework: 'React' },
  // Angular
  { pattern: /ng-version="/i,             framework: 'Angular' },
  { pattern: /<app-root/i,                framework: 'Angular' },
  // Vue
  { pattern: /id="app".*data-v-/i,        framework: 'Vue.js' },
  { pattern: /data-v-[a-f0-9]{8}/i,       framework: 'Vue.js' },
  // Svelte / SvelteKit
  { pattern: /svelte-[a-z0-9]/i,          framework: 'Svelte' },
  { pattern: /__sveltekit/,               framework: 'SvelteKit' },
  // Shopify
  { pattern: /Shopify\.theme/i,           framework: 'Shopify' },
  { pattern: /shopify-section/i,          framework: 'Shopify' },
  // Wix
  { pattern: /wix-dropdown-menu/i,        framework: 'Wix' },
  { pattern: /X-Wix-/i,                   framework: 'Wix' },
  // Squarespace
  { pattern: /squarespace-headers/i,      framework: 'Squarespace' },
  { pattern: /Static\.SQUARESPACE/i,      framework: 'Squarespace' },
  // Webflow
  { pattern: /data-wf-site/i,             framework: 'Webflow' },
  { pattern: /webflow\.js/i,              framework: 'Webflow' },
  // Gatsby
  { pattern: /___gatsby/i,                framework: 'Gatsby' },
  // Drupal
  { pattern: /\/sites\/default\/files/i,  framework: 'Drupal' },
];

const FRAMEWORK_SCRIPT_PATTERNS = [
  { match: /\/wp-includes\//i,               framework: 'WordPress' },
  { match: /\/wp-content\//i,                framework: 'WordPress' },
  { match: /cdn\.shopify\.com\/s\/files/i,   framework: 'Shopify' },
  { match: /platform\.squarespace\.com/i,    framework: 'Squarespace' },
  { match: /cdn\.webflow\.com/i,             framework: 'Webflow' },
  { match: /static\.wixstatic\.com/i,        framework: 'Wix' },
  { match: /gatsby-chunk-mapping/i,          framework: 'Gatsby' },
  { match: /\.vue\.js/i,                     framework: 'Vue.js' },
  { match: /angular(\.min)?\.js/i,           framework: 'Angular' },
];

const FRAMEWORK_HEADER_SIGNALS = [
  { header: 'x-powered-by', match: /express/i,      framework: 'Express (Node.js)' },
  { header: 'x-powered-by', match: /asp\.net/i,     framework: 'ASP.NET' },
  { header: 'x-powered-by', match: /php/i,          framework: 'PHP' },
  { header: 'x-powered-by', match: /next\.js/i,     framework: 'Next.js' },
  { header: 'x-powered-by', match: /nuxt/i,         framework: 'Nuxt.js' },
  { header: 'x-drupal-cache',                        framework: 'Drupal' },
  { header: 'x-generator',  match: /drupal/i,       framework: 'Drupal' },
  { header: 'x-shopify-stage',                       framework: 'Shopify' },
  { header: 'x-wix-request-id',                      framework: 'Wix' },
];

const ADDITIONAL_TECH_PATTERNS = [
  { match: /google-analytics\.com|gtag\/js|ga\.js/i,    tech: 'Google Analytics' },
  { match: /googletagmanager\.com/i,                     tech: 'Google Tag Manager' },
  { match: /cdn\.segment\.com/i,                         tech: 'Segment' },
  { match: /hotjar\.com/i,                               tech: 'Hotjar' },
  { match: /react(-dom)?(\.|\/)/i,                       tech: 'React' },
  { match: /vue(\.runtime)?(\.min)?\.js/i,               tech: 'Vue.js' },
  { match: /jquery(\.min)?\.js/i,                        tech: 'jQuery' },
  { match: /bootstrap(\.min)?\.(js|css)/i,               tech: 'Bootstrap' },
  { match: /tailwindcss|tailwind/i,                      tech: 'Tailwind CSS' },
  { match: /stripe\.com\/v3/i,                           tech: 'Stripe' },
  { match: /connect\.facebook\.net/i,                    tech: 'Facebook SDK' },
  { match: /cloudflare-static/i,                         tech: 'Cloudflare' },
  { match: /intercom/i,                                  tech: 'Intercom' },
  { match: /zendesk/i,                                   tech: 'Zendesk' },
  { match: /hubspot/i,                                   tech: 'HubSpot' },
  { match: /crisp\.chat/i,                               tech: 'Crisp' },
];

// ---------------------------------------------------------------------------
// Main analyzer
// ---------------------------------------------------------------------------

export function analyzeSiteProfile(crawlResult) {
  const hostingSet = new Set();
  const frameworkSet = new Set();
  const techSet = new Set();
  let serverName = null;

  for (const page of crawlResult.pages) {
    const headers = page.headers || {};
    const html = page.html || '';
    const $ = page.$;

    // ----- Hosting from headers -----
    for (const fp of HOSTING_HEADERS) {
      const val = headers[fp.header];
      if (val) {
        if (!fp.match || fp.match.test(val)) {
          hostingSet.add(fp.hosting);
        }
      }
    }

    // Cloudflare cf-ray special case
    if (headers['cf-ray']) {
      hostingSet.add('Cloudflare');
    }

    // Server header
    const serverHeader = headers['server'] || '';
    if (serverHeader) {
      for (const sv of HOSTING_SERVER_VALUES) {
        if (sv.match.test(serverHeader)) {
          if (sv.hosting) hostingSet.add(sv.hosting);
          if (sv.server && !serverName) serverName = sv.server;
        }
      }
    }

    // ----- Hosting from cookies -----
    const setCookie = headers['set-cookie'] || '';
    for (const ck of HOSTING_COOKIE_SIGNALS) {
      if (setCookie.includes(ck.cookie) && ck.hosting) {
        hostingSet.add(ck.hosting);
      }
    }

    // ----- Framework from headers -----
    for (const fp of FRAMEWORK_HEADER_SIGNALS) {
      const val = headers[fp.header];
      if (val) {
        if (!fp.match || fp.match.test(val)) {
          frameworkSet.add(fp.framework);
        }
      }
    }

    // ----- Framework from meta generator -----
    if ($) {
      const generator = $('meta[name="generator"]').attr('content') || '';
      if (generator) {
        for (const fp of FRAMEWORK_META_GENERATOR) {
          if (fp.match.test(generator)) {
            frameworkSet.add(fp.framework);
          }
        }
      }
    }

    // ----- Framework from HTML markers -----
    for (const fp of FRAMEWORK_HTML_MARKERS) {
      if (fp.pattern.test(html)) {
        frameworkSet.add(fp.framework);
      }
    }

    // ----- Hosting + Framework from script/link URLs -----
    if ($) {
      const urls = [];
      $('script[src]').each((_, el) => {
        const src = $(el).attr('src');
        if (src) urls.push(src);
      });
      $('link[href]').each((_, el) => {
        const href = $(el).attr('href');
        if (href) urls.push(href);
      });

      for (const u of urls) {
        for (const fp of HOSTING_URL_PATTERNS) {
          if (fp.match.test(u)) {
            hostingSet.add(fp.hosting);
          }
        }
        for (const fp of FRAMEWORK_SCRIPT_PATTERNS) {
          if (fp.match.test(u)) {
            frameworkSet.add(fp.framework);
          }
        }
        for (const fp of ADDITIONAL_TECH_PATTERNS) {
          if (fp.match.test(u)) {
            techSet.add(fp.tech);
          }
        }
      }

      // Also check inline scripts for tech patterns
      $('script:not([src])').each((_, el) => {
        const text = $(el).html() || '';
        for (const fp of ADDITIONAL_TECH_PATTERNS) {
          if (fp.match.test(text)) {
            techSet.add(fp.tech);
          }
        }
      });
    }
  }

  // Remove framework names that also appear in tech (avoid duplication)
  for (const fw of frameworkSet) {
    techSet.delete(fw);
  }

  // Build result
  const hosting = Array.from(hostingSet);
  const framework = Array.from(frameworkSet);
  const additionalTech = Array.from(techSet);

  return {
    hosting: hosting.length > 0 ? hosting : ['Unknown'],
    framework: framework.length > 0 ? framework : ['Unknown'],
    server: serverName || null,
    additionalTech,
  };
}
