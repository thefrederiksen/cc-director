// A 1-based pager, ported from the Blazor Cockpit Pager.razor. It renders nothing when the whole set
// fits on one page (totalCount <= pageSize), and otherwise shows First / Prev / an info span /
// Next / Last with the ends disabled at the boundaries. The parent owns the page number; this
// component only reports the requested page through onPageChange.

export interface PagerProps {
  page: number;
  pageSize: number;
  totalCount: number;
  onPageChange: (page: number) => void;
}

export function Pager({ page, pageSize, totalCount, onPageChange }: PagerProps) {
  if (totalCount <= pageSize) return null;

  const pageCount = Math.max(1, Math.ceil(totalCount / pageSize));
  const rangeStart = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const rangeEnd = Math.min(page * pageSize, totalCount);

  // Clamp any requested page into [1, pageCount] before reporting it, so the parent never holds an
  // out-of-range page.
  const go = (target: number) => {
    const clamped = Math.min(Math.max(1, target), pageCount);
    if (clamped !== page) onPageChange(clamped);
  };

  return (
    <div className="pager">
      <button type="button" className="pager-btn" onClick={() => go(1)} disabled={page <= 1} aria-label="First page">
        |&lt;
      </button>
      <button type="button" className="pager-btn" onClick={() => go(page - 1)} disabled={page <= 1}>
        &lt; Prev
      </button>
      <span className="pager-info">
        {rangeStart}-{rangeEnd} of {totalCount} &middot; page {page} of {pageCount}
      </span>
      <button type="button" className="pager-btn" onClick={() => go(page + 1)} disabled={page >= pageCount}>
        Next &gt;
      </button>
      <button type="button" className="pager-btn" onClick={() => go(pageCount)} disabled={page >= pageCount} aria-label="Last page">
        &gt;|
      </button>
    </div>
  );
}
