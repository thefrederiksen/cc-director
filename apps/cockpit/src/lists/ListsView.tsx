import { useCallback, useEffect, useRef, useState } from "react";
import {
  appendWorkListItem,
  createWorkList,
  getWorkLists,
  removeWorkListItem,
  reorderWorkListItems,
  type WorkList,
  type WorkListItemRef,
} from "@devthrottle/client-core/lists/listsClient";
import { resolveItemStatus, type WorkItemInfo, type WorkItemStatus } from "@devthrottle/client-core/api/itemStatus";
import { clockLabel } from "../fleet/format";

// The Lists page (issue #977, epic #967) - the React port of the Blazor Cockpit Lists.razor (#275).
// The human's window into the named work lists: a pure CLIENT of the Gateway's /lists object (every
// create / append / reorder / remove goes through client-core to the Gateway, so the Cockpit never
// owns a copy or its own ordering). Per-item title + flow:* status are resolved through the Gateway
// item-status endpoint (issue #970) - never a browser-held GitHub token. Reads and writes are
// same-origin, root-relative through the Gateway front door - never a Director address.
//
// Polling matches the Blazor page: a slow 10s refresh (lists change on human action, and per-item
// status follows GitHub labels which move on the order of seconds-to-minutes), which never clobbers
// an in-flight modal or add-item edit.
const POLL_MS = 10000;

// The per-item key ("source/id") the info cache and the drag/move helpers use - matches Blazor Key().
function keyOf(item: WorkListItemRef): string {
  return `${item.source}/${item.id}`;
}

const RESOLVING: WorkItemInfo = { title: null, status: "unknown", detail: "resolving..." };

export function ListsView() {
  const [lists, setLists] = useState<WorkList[]>([]);
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [itemError, setItemError] = useState<string | null>(null);

  // Per-item GitHub-derived info (title + status), keyed by "source/id". Recomputed on each refresh,
  // never stored alongside the list - so the badge always follows the label (Blazor _info).
  const [info, setInfo] = useState<Record<string, WorkItemInfo>>({});

  // create-list modal
  const [showCreate, setShowCreate] = useState(false);
  const [creating, setCreating] = useState(false);
  const [createName, setCreateName] = useState("");
  const [createError, setCreateError] = useState<string | null>(null);

  // add-item form
  const [adding, setAdding] = useState(false);
  const [newSource, setNewSource] = useState("github");
  const [newId, setNewId] = useState("");
  const [newArea, setNewArea] = useState("");

  // drag-reorder
  const dragItem = useRef<WorkListItemRef | null>(null);

  // A modal/add edit is in flight -> the background poll skips a refresh so it never clobbers the
  // human mid-action (mirrors the Blazor page never refreshing over an open modal/add).
  const busyRef = useRef(false);
  busyRef.current = showCreate || creating || adding;

  const selected = lists.find((l) => l.name.toLowerCase() === (selectedName ?? "").toLowerCase()) ?? null;

  // Resolve title + status for every item across all lists (so the cards' done-counts and the
  // selected list's rows are both accurate). Never throws - resolveItemStatus returns unknown on a
  // GitHub failure, so a flaky GitHub never breaks the list view (the no-fallback "unknown" badge).
  const resolveInfo = useCallback(async (fresh: WorkList[], signal?: AbortSignal) => {
    const refs = new Map<string, WorkListItemRef>();
    for (const l of fresh) for (const i of l.items) if (!refs.has(keyOf(i))) refs.set(keyOf(i), i);
    const next: Record<string, WorkItemInfo> = {};
    for (const [k, r] of refs) {
      try {
        next[k] = await resolveItemStatus(r.source, r.id, signal);
      } catch {
        next[k] = { title: null, status: "unknown", detail: null };
      }
    }
    if (signal?.aborted !== true) setInfo(next);
  }, []);

  const refresh = useCallback(
    async (signal?: AbortSignal) => {
      if (busyRef.current) return;
      try {
        const fresh = await getWorkLists(signal);
        setLists(fresh);
        setLastError(null);
        setLastRefresh(new Date());
        // Auto-select the first list when nothing is chosen yet (Blazor RefreshAsync).
        setSelectedName((cur) => (cur === null && fresh.length > 0 ? fresh[0].name : cur));
        await resolveInfo(fresh, signal);
      } catch (err) {
        if (signal?.aborted === true) return;
        setLastError(err instanceof Error ? err.message : "Failed to fetch lists");
      }
    },
    [resolveInfo],
  );

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => void refresh(controller.signal), POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [refresh]);

  const infoFor = (item: WorkListItemRef): WorkItemInfo => info[keyOf(item)] ?? RESOLVING;

  const selectList = (name: string) => {
    setSelectedName(name);
    setItemError(null);
  };

  // ---- create list ----
  const openCreate = () => {
    setCreateName("");
    setCreateError(null);
    setShowCreate(true);
  };

  const createList = async () => {
    const name = createName.trim();
    if (name.length === 0) return;
    setCreating(true);
    setCreateError(null);
    try {
      await createWorkList(name);
      setShowCreate(false);
      setSelectedName(name); // select the just-created list once the refresh brings it in
      busyRef.current = false;
      await refresh();
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : "Create failed");
    } finally {
      setCreating(false);
    }
  };

  // ---- add item ----
  const addItem = async () => {
    if (selected === null || newId.trim().length === 0) return;
    setAdding(true);
    setItemError(null);
    const listName = selected.name;
    try {
      const item: WorkListItemRef = {
        source: newSource,
        id: newId.trim(),
        area: newArea.trim().length === 0 ? null : newArea.trim(),
      };
      await appendWorkListItem(listName, item);
      setNewId("");
      setNewArea("");
      busyRef.current = false;
      await refresh();
    } catch (err) {
      setItemError(`Add item failed: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setAdding(false);
    }
  };

  // ---- remove item ----
  const removeItem = async (item: WorkListItemRef) => {
    if (selected === null) return;
    setItemError(null);
    const listName = selected.name;
    try {
      await removeWorkListItem(listName, item.source, item.id);
      await refresh();
    } catch (err) {
      setItemError(`Remove item failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  };

  // ---- reorder (commit the full ordered array via PATCH - never a local-only reorder) ----
  const commitOrder = async (order: WorkListItemRef[]) => {
    if (selected === null) return;
    setItemError(null);
    const listName = selected.name;
    try {
      await reorderWorkListItems(listName, order);
      await refresh();
    } catch (err) {
      setItemError(`Reorder failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  };

  const onDrop = async (target: WorkListItemRef) => {
    const dragged = dragItem.current;
    dragItem.current = null;
    if (dragged === null || selected === null || keyOf(dragged) === keyOf(target)) return;
    const order = [...selected.items];
    const from = order.findIndex((i) => keyOf(i) === keyOf(dragged));
    const to = order.findIndex((i) => keyOf(i) === keyOf(target));
    if (from < 0 || to < 0 || from === to) return;
    order.splice(from, 1);
    order.splice(to, 0, dragged);
    await commitOrder(order);
  };

  const move = async (item: WorkListItemRef, delta: number) => {
    if (selected === null) return;
    const order = [...selected.items];
    const i = order.findIndex((x) => keyOf(x) === keyOf(item));
    const j = i + delta;
    if (i < 0 || j < 0 || j >= order.length) return;
    [order[i], order[j]] = [order[j], order[i]];
    await commitOrder(order);
  };

  return (
    <div className="page lists-page">
      <header className="page-head">
        <h1>Lists</h1>
        <span className="page-sub">named priority lists, drained by the loop</span>
        <span className="page-refreshed">
          {lastRefresh === null ? "connecting..." : `updated ${clockLabel(lastRefresh)}`}
        </span>
        <button className="btn primary" onClick={openCreate}>
          New list
        </button>
      </header>

      {lastError !== null && <div className="page-banner-error">Gateway error: {lastError}</div>}

      <div className="lists-cols">
        {/* ---- list-card column ---- */}
        <div className="listcol">
          <div className="listcol-lbl">Lists</div>

          {lists.length === 0 && lastError === null && lastRefresh !== null && (
            <div className="listcol-empty">No lists yet. Create one to start a priority queue.</div>
          )}

          {lists.map((l) => (
            <button
              key={l.name}
              className={`lcard${l.name.toLowerCase() === (selectedName ?? "").toLowerCase() ? " active" : ""}`}
              onClick={() => selectList(l.name)}
            >
              <div className="lcard-nm">
                <span className="lcard-name">{l.name}</span>
                {l.consumer && l.consumer.length > 0 ? (
                  <span className="runtag run">RUNNING</span>
                ) : (
                  <span className="runtag idle">IDLE</span>
                )}
              </div>
              <div className="lcard-mt">{cardMeta(l, infoFor)}</div>
            </button>
          ))}

          <button className="newlist" onClick={openCreate}>
            + New list
          </button>
          <div className="listcol-note">
            A list is just a named priority list - it can hold anything across any area, source, or repo.
            Any free machine can claim and run it.
          </div>
        </div>

        {/* ---- items column ---- */}
        <div className="itemscol">
          {selected === null ? (
            <div className="items-empty">Select a list on the left, or create one, to see its items.</div>
          ) : (
            <>
              <div className="ihead">
                <div>
                  <h2>{selected.name}</h2>
                  <div className="ihead-meta">
                    {selected.consumer && selected.consumer.length > 0 ? (
                      <>
                        claimed by <b>{selected.consumer}</b> &middot; one item at a time
                      </>
                    ) : (
                      <>unclaimed &middot; run anywhere</>
                    )}
                  </div>
                </div>
                <div className="prog">
                  <div className="prog-row">
                    <span>Progress</span>
                    <span>{progressLine(selected, infoFor)}</span>
                  </div>
                  <div className="bar">
                    <i style={{ width: `${donePct(selected, infoFor)}%` }} />
                  </div>
                </div>
              </div>

              {itemError !== null && <div className="page-banner-error">{itemError}</div>}

              {selected.items.length === 0 ? (
                <div className="items-empty">This list is empty. Add an item by id + source below.</div>
              ) : (
                <table className="itable">
                  <thead>
                    <tr>
                      <th style={{ width: 30 }} />
                      <th style={{ width: 42 }}>Pri</th>
                      <th style={{ width: 54 }}>Src</th>
                      <th style={{ width: 72 }}>Id</th>
                      <th>Title</th>
                      <th style={{ width: 130 }}>Status</th>
                      <th style={{ width: 76 }} />
                    </tr>
                  </thead>
                  <tbody>
                    {selected.items.map((item, idx) => {
                      const prio = idx + 1;
                      const it = infoFor(item);
                      return (
                        <tr
                          key={keyOf(item)}
                          className={it.status === "running" ? "running" : ""}
                          draggable
                          onDragStart={() => {
                            dragItem.current = item;
                          }}
                          onDragOver={(e) => e.preventDefault()}
                          onDrop={() => void onDrop(item)}
                        >
                          <td className="ihandle" title="Drag to reprioritize">
                            ::
                          </td>
                          <td>
                            <span className="prio">{prio}</span>
                          </td>
                          <td>
                            <span className={`src ${srcClass(item.source)}`}>{srcLabel(item.source)}</span>
                          </td>
                          <td className="idc">{displayId(item)}</td>
                          <td>
                            <div className="ittl">{it.title ?? "(title unavailable)"}</div>
                            {item.area && item.area.trim().length > 0 && (
                              <div className="isubline">
                                <span className="areatag">
                                  <span className={`adot ${areaClass(item.area)}`} />
                                  {item.area}
                                </span>
                              </div>
                            )}
                          </td>
                          <td>
                            <span
                              className={`st ${statusClass(it.status)}`}
                              title={it.detail ?? statusTooltip(it.status)}
                            >
                              {statusLabel(it.status)}
                            </span>
                          </td>
                          <td className="iactions">
                            <button
                              className="iarrow"
                              title="Move up"
                              disabled={prio === 1}
                              onClick={() => void move(item, -1)}
                            >
                              &#8593;
                            </button>
                            <button
                              className="iarrow"
                              title="Move down"
                              disabled={prio === selected.items.length}
                              onClick={() => void move(item, 1)}
                            >
                              &#8595;
                            </button>
                            <button className="iarrow del" title="Remove from list" onClick={() => void removeItem(item)}>
                              &times;
                            </button>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )}

              {/* ---- add item row ---- */}
              <div className="additem">
                <span className="additem-lbl">Add item</span>
                <select className="additem-src" value={newSource} onChange={(e) => setNewSource(e.target.value)}>
                  <option value="github">GitHub</option>
                  <option value="devops">DevOps</option>
                  <option value="jira">JIRA</option>
                </select>
                <input
                  className="additem-id"
                  placeholder="id (e.g. 262, 1203, CCD-44)"
                  value={newId}
                  onChange={(e) => setNewId(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void addItem();
                  }}
                />
                <input
                  className="additem-area"
                  placeholder="area (optional)"
                  value={newArea}
                  onChange={(e) => setNewArea(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void addItem();
                  }}
                />
                <button className="btn" disabled={adding || newId.trim().length === 0} onClick={() => void addItem()}>
                  {adding ? "Adding..." : "Add"}
                </button>
              </div>

              <div className="items-note">
                This list can mix Gateway, Core, Installer, and Cockpit items across GitHub, DevOps, and
                JIRA - a list is not sliced by area or source. Drag the handle (or the arrows) to
                reprioritize. Status follows each github item's flow:* label, never a local copy.
              </div>
            </>
          )}
        </div>
      </div>

      {/* ---- create-list modal ---- */}
      {showCreate && (
        <div className="modal-backdrop" onClick={() => setShowCreate(false)}>
          <div className="modal" onClick={(e) => e.stopPropagation()}>
            <div className="modal-head">New list</div>
            <div className="modal-body">
              <div className="fld">
                <label className="fld-label">List name</label>
                <input
                  value={createName}
                  autoFocus
                  placeholder="e.g. Today, Release 0.7"
                  onChange={(e) => setCreateName(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" && createName.trim().length > 0) void createList();
                  }}
                />
              </div>
              {createError !== null && <div className="modal-error">{createError}</div>}
            </div>
            <div className="modal-foot">
              <button className="btn" onClick={() => setShowCreate(false)}>
                Cancel
              </button>
              <button
                className="btn primary"
                disabled={creating || createName.trim().length === 0}
                onClick={() => void createList()}
              >
                {creating ? "Creating..." : "Create"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ---- display helpers (faithful ports of the Blazor private helpers) ----

function cardMeta(l: WorkList, infoFor: (i: WorkListItemRef) => WorkItemInfo): string {
  const done = l.items.filter((i) => infoFor(i).status === "done").length;
  const claim = l.consumer && l.consumer.length > 0 ? `claimed by ${l.consumer}` : "unclaimed";
  const n = l.items.length;
  return `${n} item${n === 1 ? "" : "s"} · ${done} done · ${claim}`;
}

function progressLine(l: WorkList, infoFor: (i: WorkListItemRef) => WorkItemInfo): string {
  const done = l.items.filter((i) => infoFor(i).status === "done").length;
  const running = l.items.filter((i) => infoFor(i).status === "running").length;
  const queued = l.items.length - done - running;
  return `${done} done · ${running} running · ${Math.max(0, queued)} queued`;
}

function donePct(l: WorkList, infoFor: (i: WorkListItemRef) => WorkItemInfo): number {
  if (l.items.length === 0) return 0;
  const done = l.items.filter((i) => infoFor(i).status === "done").length;
  return Math.round((100 * done) / l.items.length);
}

function displayId(item: WorkListItemRef): string {
  return item.source.toLowerCase() === "github" ? `#${item.id}` : item.id;
}

function srcLabel(source: string): string {
  switch (source.toLowerCase()) {
    case "github":
      return "GH";
    case "devops":
      return "DO";
    case "jira":
      return "JIRA";
    default:
      return source.toUpperCase();
  }
}

function srcClass(source: string): string {
  switch (source.toLowerCase()) {
    case "github":
      return "gh";
    case "devops":
      return "do";
    case "jira":
      return "jira";
    default:
      return "gh";
  }
}

function areaClass(area: string | null | undefined): string {
  switch ((area ?? "").toLowerCase()) {
    case "gateway":
      return "a-gateway";
    case "core":
      return "a-core";
    case "installer":
      return "a-installer";
    case "cockpit":
      return "a-cockpit";
    default:
      return "a-other";
  }
}

function statusLabel(s: WorkItemStatus): string {
  switch (s) {
    case "queued":
      return "QUEUED";
    case "running":
      return "RUNNING";
    case "done":
      return "DONE";
    case "needs-human":
      return "NEEDS YOU";
    case "failed":
      return "FAILED";
    default:
      return "UNKNOWN";
  }
}

function statusClass(s: WorkItemStatus): string {
  switch (s) {
    case "queued":
      return "queued";
    case "running":
      return "running";
    case "done":
      return "done";
    case "needs-human":
      return "needs";
    case "failed":
      return "failed";
    default:
      return "unknown";
  }
}

function statusTooltip(s: WorkItemStatus): string {
  switch (s) {
    case "queued":
      return "Queued - waiting to be drained";
    case "running":
      return "Running - an implementation loop is on this item";
    case "done":
      return "Done - flow:done";
    case "needs-human":
      return "Needs you - flow:needs-human";
    case "failed":
      return "Failed";
    default:
      return "Status unavailable";
  }
}
