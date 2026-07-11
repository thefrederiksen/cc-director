// The pure sort-and-filter core of the shared DataTable (issue #1245). These functions hold no React
// state and touch no DOM, so the table's behaviour - "typing in the search box filters", "clicking a
// header sorts" - is unit-tested here directly (dataTableCore.test.ts) without rendering anything. The
// DataTable component (DataTable.tsx) is a thin view over exactly these two operations.

/** Ascending or descending sort on a column. */
export type SortDirection = "asc" | "desc";

/** The active sort: which column, and which way. */
export interface SortState {
  columnKey: string;
  direction: SortDirection;
}

// Does one row's searchable text contain the query? The query is trimmed and compared
// case-insensitively; an empty query matches every row (the search box is not yet narrowing).
export function matchesQuery(searchableText: string, query: string): boolean {
  const q = query.trim().toLowerCase();
  if (q.length === 0) return true;
  return searchableText.toLowerCase().includes(q);
}

// Compare two sort values for one column. Numbers compare numerically (so "in 46m" sorts before
// "in 3h" when both are backed by an epoch number, not by their display text); anything else compares
// as a case-insensitive string. The result is negated for a descending sort. This is a pure total
// order, so callers get a stable, predictable arrangement.
export function compareSortValues(
  a: string | number,
  b: string | number,
  direction: SortDirection,
): number {
  let comparison: number;
  if (typeof a === "number" && typeof b === "number") {
    comparison = a - b;
  } else {
    comparison = String(a).localeCompare(String(b), undefined, { sensitivity: "base" });
  }
  return direction === "asc" ? comparison : -comparison;
}

// Filter rows by the search query using each row's searchable text, then sort by the active column's
// sort value. The sort is stable: rows that compare equal keep their input order (a plain index
// tie-breaker), so a second sort key never scrambles the first. `sortValueFor` returns the value to
// sort a row by for the active column, or null when the active column is not sortable (in which case
// the rows keep their filtered order).
export function filterAndSortRows<T>(
  rows: readonly T[],
  query: string,
  searchableText: (row: T) => string,
  sort: SortState | null,
  sortValueFor: (row: T, columnKey: string) => string | number | null,
): T[] {
  const filtered = rows.filter((row) => matchesQuery(searchableText(row), query));
  if (sort === null) return filtered;

  // Decorate with the original index so equal rows keep their relative order (a stable sort).
  const decorated = filtered.map((row, index) => ({ row, index }));
  decorated.sort((left, right) => {
    const a = sortValueFor(left.row, sort.columnKey);
    const b = sortValueFor(right.row, sort.columnKey);
    if (a === null || b === null) return left.index - right.index;
    const comparison = compareSortValues(a, b, sort.direction);
    return comparison !== 0 ? comparison : left.index - right.index;
  });
  return decorated.map((entry) => entry.row);
}

// The next sort state when a header is clicked. Clicking a new column sorts it ascending; clicking the
// already-sorted column flips its direction. This is the one place the header-click rule lives, so the
// component and its tests agree on it.
export function nextSort(current: SortState | null, columnKey: string): SortState {
  if (current !== null && current.columnKey === columnKey) {
    return { columnKey, direction: current.direction === "asc" ? "desc" : "asc" };
  }
  return { columnKey, direction: "asc" };
}
