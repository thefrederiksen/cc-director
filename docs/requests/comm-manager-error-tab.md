# Request: Add Error Tab to Communication Manager UI

## Context

The Communication Manager WPF app has 4 tabs: Pending, Approved, Rejected, Sent. We just added an `error` status to the comm queue for items that failed to send (e.g., LinkedIn profile 404). The UI needs an Error tab to display these items.

## What Changed (Already Done)

1. `tools/cc-comm-queue/src/schema.py` -- Added `ERROR = "error"` to the `Status` enum, added `error: int = 0` to `QueueStats`
2. `tools/cc-comm-queue/src/queue_manager.py` -- Added `mark_error()` method
3. `tools/cc-comm-queue/src/cli.py` -- Added `mark-error` CLI command, added `error` to status filter map

## What Needs to Change (UI)

### 1. ContentItem.cs Status Constants

**File:** `src/CcDirector.Core/Communications/Models/ContentItem.cs`

Add the error status string constant wherever pending_review, approved, rejected, posted are defined.

### 2. MainViewModel.cs

**File:** `src/CcDirector.CommunicationManager/ViewModels/MainViewModel.cs`

- Add an `ErrorItems` ObservableCollection (same pattern as `PendingItems`, `ApprovedItems`, `RejectedItems`, `SentItems`)
- In the method that loads items by status, add a call to load `"error"` status items into `ErrorItems`
- The Error tab items should show the error reason (stored in `rejection_reason` field) prominently

### 3. CommunicationManagerView.xaml

**File:** `src/CcDirector.CommunicationManager/Views/CommunicationManagerView.xaml`

- Add an **Error** tab after the Rejected tab (or after Sent -- wherever makes sense visually)
- Use the same item template as other tabs but include the error reason text
- Consider a red/warning color indicator for error items

### 4. DatabaseService.cs

**File:** `src/CcDirector.CommunicationManager/Services/DatabaseService.cs`

- If status loading is hardcoded to specific values, add `"error"` to the list
- The `LoadItemsByStatusAsync("error")` call should work if the DB layer is generic

### 5. ContentService.cs

**File:** `src/CcDirector.CommunicationManager/Services/ContentService.cs`

- Add `LoadErrorItemsAsync()` method (same pattern as other status loaders)

## Error Item Display

Each error item should show:
- Recipient name, title, company
- The content that was supposed to be sent
- The **error reason** (from `rejection_reason` field) -- this is the key difference from other tabs
- Timestamp of when the error occurred (from `rejected_at` field)

## Actions on Error Items

- **Retry** -- move back to approved status for another attempt
- **Delete** -- remove from queue entirely

## Build Note

The cc-comm-queue exe also needs rebuilding to include the new `mark-error` command. The tool's build script is at `tools/cc-comm-queue/build.ps1` or use `scripts/build-tools.bat`.

## Testing

1. Run `cc-comm-queue list --status error` to confirm error items exist
2. Open Communication Manager
3. Verify Error tab shows the 2 items (Chloe Duteil and Sohail Uberoi)
4. Verify error reason is displayed: "LinkedIn profile 404 - page does not exist"
