"""IMAP client for Gmail with GIMAP extension support.

Uses Python's imaplib to connect to imap.gmail.com with Gmail-specific
IMAP extensions (X-GM-RAW for search, X-GM-LABELS, X-GM-THRID for threads).
"""

import email
import email.message
import imaplib
import logging
import re
from email.header import decode_header
from email.utils import parseaddr
from typing import Optional, List, Dict, Any

logger = logging.getLogger(__name__)

# Gmail IMAP server
IMAP_HOST = "imap.gmail.com"
IMAP_PORT = 993

# Gmail label to IMAP folder mapping
GMAIL_FOLDERS = {
    "INBOX": "INBOX",
    "SENT": "[Gmail]/Sent Mail",
    "DRAFT": "[Gmail]/Drafts",
    "SPAM": "[Gmail]/Spam",
    "TRASH": "[Gmail]/Trash",
    "STARRED": "[Gmail]/Starred",
    "IMPORTANT": "[Gmail]/Important",
    "ALL": "[Gmail]/All Mail",
}


def _decode_header_value(value: str) -> str:
    """Decode an email header value that may be MIME-encoded."""
    if not value:
        return ""
    decoded_parts = []
    for part, charset in decode_header(value):
        if isinstance(part, bytes):
            charset = charset or "utf-8"
            try:
                decoded_parts.append(part.decode(charset, errors="replace"))
            except (LookupError, UnicodeDecodeError):
                decoded_parts.append(part.decode("utf-8", errors="replace"))
        else:
            decoded_parts.append(part)
    return " ".join(decoded_parts)


def _parse_uid_from_response(data: bytes) -> Optional[str]:
    """Extract UID from an IMAP FETCH response line."""
    match = re.search(rb"UID (\d+)", data)
    if match:
        return match.group(1).decode("ascii")
    return None


class ImapClient:
    """Gmail IMAP client with GIMAP extension support."""

    def __init__(self, email_address: str, app_password: str):
        """Initialize IMAP client.

        Args:
            email_address: Gmail address to connect with.
            app_password: App password for authentication.
        """
        self.email_address = email_address
        self.app_password = app_password
        self._conn: Optional[imaplib.IMAP4_SSL] = None

    def _connect(self) -> imaplib.IMAP4_SSL:
        """Get or create an IMAP connection."""
        if self._conn is not None:
            try:
                self._conn.noop()
                return self._conn
            except (imaplib.IMAP4.error, OSError):
                self._conn = None

        conn = imaplib.IMAP4_SSL(IMAP_HOST, IMAP_PORT)
        conn.login(self.email_address, self.app_password)
        self._conn = conn
        return conn

    def close(self) -> None:
        """Close the IMAP connection."""
        if self._conn is not None:
            try:
                self._conn.close()
                self._conn.logout()
            except (imaplib.IMAP4.error, OSError):
                pass
            self._conn = None

    def _select_folder(self, folder: str = "INBOX", readonly: bool = True) -> None:
        """Select an IMAP folder/mailbox."""
        conn = self._connect()
        # Map Gmail label names to IMAP folder names
        imap_folder = GMAIL_FOLDERS.get(folder.upper(), folder)
        # Quote the folder name for IMAP
        status, data = conn.select(f'"{imap_folder}"', readonly=readonly)
        if status != "OK":
            raise ConnectionError(f"Failed to select folder '{folder}': {data}")

    def get_profile(self) -> Dict[str, Any]:
        """Get basic profile info (email address)."""
        return {
            "emailAddress": self.email_address,
        }

    def list_messages(
        self,
        label_ids: Optional[List[str]] = None,
        query: Optional[str] = None,
        max_results: int = 10,
        include_spam_trash: bool = False,
    ) -> List[Dict[str, Any]]:
        """List messages matching criteria using Gmail IMAP extensions.

        Args:
            label_ids: Filter by label IDs (e.g., ["INBOX", "UNREAD"]).
            query: Gmail search query string (uses X-GM-RAW).
            max_results: Maximum number of messages to return.
            include_spam_trash: Include messages from SPAM and TRASH.

        Returns:
            List of message dicts with 'id' (UID) and basic info.
        """
        conn = self._connect()

        # Determine which folder to select
        folder = "INBOX"
        is_unread_filter = False
        if label_ids:
            for label in label_ids:
                upper = label.upper()
                if upper == "UNREAD":
                    is_unread_filter = True
                elif upper in GMAIL_FOLDERS:
                    folder = upper
                elif upper.startswith("CATEGORY_"):
                    # Category filtering is done via search
                    pass
                else:
                    folder = label

        if include_spam_trash:
            folder = "ALL"

        self._select_folder(folder, readonly=True)

        # Build search criteria
        search_parts = []
        if is_unread_filter:
            search_parts.append("UNSEEN")
        if query:
            # Use Gmail's X-GM-RAW extension for full Gmail search syntax
            search_parts.append(f'X-GM-RAW "{query}"')

        # Handle category filtering from label_ids
        if label_ids:
            for label in label_ids:
                upper = label.upper()
                if upper.startswith("CATEGORY_"):
                    cat_name = upper.replace("CATEGORY_", "").lower()
                    if search_parts:
                        # Combine with existing X-GM-RAW or add new one
                        search_parts.append(f'X-GM-RAW "category:{cat_name}"')
                    else:
                        search_parts.append(f'X-GM-RAW "category:{cat_name}"')

        if not search_parts:
            search_criteria = "ALL"
        else:
            search_criteria = " ".join(search_parts)

        status, data = conn.uid("search", None, search_criteria)
        if status != "OK":
            return []

        uids = data[0].split()
        if not uids:
            return []

        # Return most recent first (highest UIDs), limited to max_results
        uids = list(reversed(uids))[:max_results]

        messages = []
        for uid in uids:
            messages.append({"id": uid.decode("ascii")})

        return messages

    def list_all_messages(
        self,
        label_ids: Optional[List[str]] = None,
        query: Optional[str] = None,
        max_results: Optional[int] = None,
    ) -> List[Dict[str, Any]]:
        """List ALL messages matching criteria (no pagination needed with IMAP).

        Args:
            label_ids: Filter by label IDs.
            query: Gmail search query.
            max_results: Optional cap on total results.

        Returns:
            List of all matching message dicts with 'id' (UID).
        """
        conn = self._connect()

        folder = "INBOX"
        is_unread_filter = False
        if label_ids:
            for label in label_ids:
                upper = label.upper()
                if upper == "UNREAD":
                    is_unread_filter = True
                elif upper in GMAIL_FOLDERS:
                    folder = upper

        self._select_folder(folder, readonly=True)

        search_parts = []
        if is_unread_filter:
            search_parts.append("UNSEEN")
        if query:
            search_parts.append(f'X-GM-RAW "{query}"')

        if not search_parts:
            search_criteria = "ALL"
        else:
            search_criteria = " ".join(search_parts)

        status, data = conn.uid("search", None, search_criteria)
        if status != "OK":
            return []

        uids = data[0].split()
        if not uids:
            return []

        uids = list(reversed(uids))
        if max_results:
            uids = uids[:max_results]

        return [{"id": uid.decode("ascii")} for uid in uids]

    def get_message_details(self, message_uid: str) -> Dict[str, Any]:
        """Get message with parsed headers and body.

        Args:
            message_uid: The message UID.

        Returns:
            Dict with id, thread_id, snippet, labels, headers, body, internal_date.
        """
        conn = self._connect()

        # Select All Mail to be able to fetch any message by UID
        self._select_folder("ALL", readonly=True)

        # Fetch the full message plus Gmail extensions
        status, data = conn.uid(
            "fetch", message_uid, "(RFC822 X-GM-THRID X-GM-LABELS)"
        )
        if status != "OK" or not data or data[0] is None:
            raise ValueError(f"Message UID {message_uid} not found")

        # Parse the response - data[0] is a tuple (envelope, message_bytes)
        raw_response = data[0]
        if isinstance(raw_response, tuple):
            envelope = raw_response[0]
            raw_email = raw_response[1]
        else:
            raise ValueError(f"Unexpected IMAP response format for UID {message_uid}")

        # Extract thread ID from envelope
        thread_id = None
        thrid_match = re.search(rb"X-GM-THRID (\d+)", envelope)
        if thrid_match:
            thread_id = thrid_match.group(1).decode("ascii")

        # Extract Gmail labels from envelope
        labels = []
        labels_match = re.search(rb'X-GM-LABELS \(([^)]*)\)', envelope)
        if labels_match:
            raw_labels = labels_match.group(1).decode("utf-8", errors="replace")
            # Parse labels (may be quoted)
            for lbl in re.findall(r'"([^"]+)"|(\S+)', raw_labels):
                label = lbl[0] or lbl[1]
                if label:
                    # Map Gmail IMAP labels to standard label IDs
                    label_upper = label.upper().strip("\\")
                    label_map = {
                        "INBOX": "INBOX",
                        "SENT": "SENT",
                        "DRAFT": "DRAFT",
                        "STARRED": "STARRED",
                        "IMPORTANT": "IMPORTANT",
                        "TRASH": "TRASH",
                        "SPAM": "SPAM",
                        "UNREAD": "UNREAD",
                    }
                    mapped = label_map.get(label_upper, label)
                    labels.append(mapped)

        # Parse the email message
        msg = email.message_from_bytes(raw_email)

        # Extract headers
        headers = {}
        for header_name in ["from", "to", "subject", "date", "cc", "bcc", "message-id", "references"]:
            value = msg.get(header_name, "")
            if value:
                headers[header_name] = _decode_header_value(value)

        # Extract body
        body = self._extract_body(msg)

        # Build snippet from body
        snippet = ""
        if body:
            clean = body.replace("\r\n", " ").replace("\n", " ").strip()
            snippet = clean[:200]

        return {
            "id": message_uid,
            "thread_id": thread_id,
            "snippet": snippet,
            "labels": labels,
            "headers": headers,
            "body": body,
            "internal_date": headers.get("date", ""),
        }

    def _extract_body(self, msg: email.message.Message) -> str:
        """Extract plain text body from an email message."""
        if not msg.is_multipart():
            content_type = msg.get_content_type()
            if content_type == "text/plain":
                payload = msg.get_payload(decode=True)
                if payload:
                    charset = msg.get_content_charset() or "utf-8"
                    return payload.decode(charset, errors="replace")
            elif content_type == "text/html":
                payload = msg.get_payload(decode=True)
                if payload:
                    charset = msg.get_content_charset() or "utf-8"
                    return payload.decode(charset, errors="replace")
            return ""

        # For multipart messages, prefer text/plain
        text_body = ""
        html_body = ""

        for part in msg.walk():
            content_type = part.get_content_type()
            content_disposition = str(part.get("Content-Disposition", ""))

            # Skip attachments
            if "attachment" in content_disposition:
                continue

            if content_type == "text/plain" and not text_body:
                payload = part.get_payload(decode=True)
                if payload:
                    charset = part.get_content_charset() or "utf-8"
                    text_body = payload.decode(charset, errors="replace")
            elif content_type == "text/html" and not html_body:
                payload = part.get_payload(decode=True)
                if payload:
                    charset = part.get_content_charset() or "utf-8"
                    html_body = payload.decode(charset, errors="replace")

        return text_body or html_body

    def search(
        self,
        query: str,
        max_results: int = 10,
        include_spam_trash: bool = False,
    ) -> List[Dict[str, Any]]:
        """Search messages using Gmail query syntax via X-GM-RAW.

        Args:
            query: Gmail search query.
            max_results: Maximum results to return.
            include_spam_trash: Include messages from SPAM and TRASH.

        Returns:
            List of matching messages.
        """
        return self.list_messages(
            query=query,
            max_results=max_results,
            include_spam_trash=include_spam_trash,
        )

    def count_messages(
        self,
        label_ids: Optional[List[str]] = None,
        query: Optional[str] = None,
    ) -> int:
        """Get count of messages matching criteria.

        Args:
            label_ids: Filter by label IDs.
            query: Gmail search query string.

        Returns:
            Count of matching messages.
        """
        conn = self._connect()

        folder = "INBOX"
        is_unread_filter = False
        if label_ids:
            for label in label_ids:
                upper = label.upper()
                if upper == "UNREAD":
                    is_unread_filter = True
                elif upper in GMAIL_FOLDERS:
                    folder = upper

        self._select_folder(folder, readonly=True)

        search_parts = []
        if is_unread_filter:
            search_parts.append("UNSEEN")
        if query:
            search_parts.append(f'X-GM-RAW "{query}"')

        if not search_parts:
            search_criteria = "ALL"
        else:
            search_criteria = " ".join(search_parts)

        status, data = conn.uid("search", None, search_criteria)
        if status != "OK":
            return 0

        uids = data[0].split()
        return len(uids)

    def mark_as_read(self, message_uid: str) -> Dict[str, Any]:
        """Mark a message as read by setting the Seen flag."""
        conn = self._connect()
        self._select_folder("ALL", readonly=False)
        conn.uid("store", message_uid, "+FLAGS", "\\Seen")
        return {"id": message_uid}

    def mark_as_unread(self, message_uid: str) -> Dict[str, Any]:
        """Mark a message as unread by removing the Seen flag."""
        conn = self._connect()
        self._select_folder("ALL", readonly=False)
        conn.uid("store", message_uid, "-FLAGS", "\\Seen")
        return {"id": message_uid}

    def archive_message(self, message_uid: str) -> Dict[str, Any]:
        """Archive a message by removing it from INBOX."""
        conn = self._connect()
        self._select_folder("INBOX", readonly=False)
        # Move out of INBOX by copying to All Mail and removing from INBOX
        conn.uid("copy", message_uid, "[Gmail]/All Mail")
        conn.uid("store", message_uid, "+FLAGS", "\\Deleted")
        conn.expunge()
        return {"id": message_uid}

    def batch_archive_messages(self, message_uids: List[str]) -> int:
        """Archive multiple messages at once.

        Args:
            message_uids: List of message UIDs to archive.

        Returns:
            Number of messages archived.
        """
        if not message_uids:
            return 0

        conn = self._connect()
        self._select_folder("INBOX", readonly=False)

        # Process in batches of 100 UIDs at a time
        archived = 0
        for i in range(0, len(message_uids), 100):
            chunk = message_uids[i:i + 100]
            uid_set = ",".join(chunk)
            conn.uid("copy", uid_set, "[Gmail]/All Mail")
            conn.uid("store", uid_set, "+FLAGS", "\\Deleted")
            archived += len(chunk)

        conn.expunge()
        return archived

    def delete_message(self, message_uid: str, permanent: bool = False) -> None:
        """Delete a message.

        Args:
            message_uid: The message UID.
            permanent: If True, permanently delete. Otherwise, move to trash.
        """
        conn = self._connect()
        self._select_folder("ALL", readonly=False)

        if permanent:
            conn.uid("store", message_uid, "+FLAGS", "\\Deleted")
            conn.expunge()
        else:
            # Move to trash
            conn.uid("copy", message_uid, "[Gmail]/Trash")
            conn.uid("store", message_uid, "+FLAGS", "\\Deleted")
            conn.expunge()

    def untrash_message(self, message_uid: str) -> Dict[str, Any]:
        """Restore a message from trash by moving to INBOX."""
        conn = self._connect()
        self._select_folder("TRASH", readonly=False)
        conn.uid("copy", message_uid, "INBOX")
        conn.uid("store", message_uid, "+FLAGS", "\\Deleted")
        conn.expunge()
        return {"id": message_uid}

    def modify_labels(
        self,
        message_uid: str,
        add_labels: Optional[List[str]] = None,
        remove_labels: Optional[List[str]] = None,
    ) -> Dict[str, Any]:
        """Modify labels on a message using Gmail IMAP extensions.

        Args:
            message_uid: The message UID.
            add_labels: Label names to add.
            remove_labels: Label names to remove.

        Returns:
            Dict with message id.
        """
        conn = self._connect()
        self._select_folder("ALL", readonly=False)

        if add_labels:
            for label in add_labels:
                imap_label = GMAIL_FOLDERS.get(label.upper(), label)
                conn.uid("copy", message_uid, f'"{imap_label}"')

        if remove_labels:
            for label in remove_labels:
                if label.upper() == "INBOX":
                    # Removing from INBOX = archiving
                    # Select INBOX, delete from there
                    self._select_folder("INBOX", readonly=False)
                    conn.uid("store", message_uid, "+FLAGS", "\\Deleted")
                    conn.expunge()

        return {"id": message_uid}

    def list_labels(self) -> List[Dict[str, Any]]:
        """List all available labels/folders."""
        conn = self._connect()
        status, data = conn.list()
        if status != "OK":
            return []

        labels = []
        for item in data:
            if item is None:
                continue
            decoded = item.decode("utf-8", errors="replace")
            # Parse IMAP LIST response: (flags) delimiter name
            match = re.match(r'\(([^)]*)\)\s+"([^"]+)"\s+"?([^"]*)"?', decoded)
            if match:
                flags = match.group(1)
                name = match.group(3).strip('"')

                # Determine label type
                is_system = name.startswith("[Gmail]") or name == "INBOX"

                # Map IMAP folder names to Gmail label IDs
                reverse_map = {v: k for k, v in GMAIL_FOLDERS.items()}
                label_id = reverse_map.get(name, name)

                labels.append({
                    "id": label_id,
                    "name": name.replace("[Gmail]/", "") if name.startswith("[Gmail]/") else name,
                    "type": "system" if is_system else "user",
                })

        return labels

    def get_label(self, label_id: str) -> Dict[str, Any]:
        """Get label details with message counts.

        Args:
            label_id: The label ID.

        Returns:
            Label dict with messagesTotal, messagesUnread, etc.
        """
        conn = self._connect()

        imap_folder = GMAIL_FOLDERS.get(label_id.upper(), label_id)

        # STATUS command gives us message counts without selecting
        status, data = conn.status(
            f'"{imap_folder}"', "(MESSAGES UNSEEN)"
        )
        if status != "OK":
            raise ValueError(f"Label '{label_id}' not found")

        # Parse response: "folder" (MESSAGES N UNSEEN M)
        response = data[0].decode("utf-8", errors="replace")
        messages_match = re.search(r"MESSAGES\s+(\d+)", response)
        unseen_match = re.search(r"UNSEEN\s+(\d+)", response)

        total = int(messages_match.group(1)) if messages_match else 0
        unseen = int(unseen_match.group(1)) if unseen_match else 0

        return {
            "id": label_id,
            "name": label_id,
            "messagesTotal": total,
            "messagesUnread": unseen,
            "threadsTotal": total,  # IMAP doesn't have thread counts
            "threadsUnread": unseen,
            "type": "system" if label_id.upper() in GMAIL_FOLDERS else "user",
        }

    def get_label_by_name(self, name: str) -> Optional[Dict[str, Any]]:
        """Get a label by its name.

        Args:
            name: The label name to search for.

        Returns:
            Label dict if found, None otherwise.
        """
        all_labels = self.list_labels()
        for label in all_labels:
            if label.get("name", "").lower() == name.lower():
                return label
        return None

    def get_or_create_label(self, name: str) -> Dict[str, Any]:
        """Get an existing label or create it.

        Args:
            name: The label name.

        Returns:
            Label dict with id and name.
        """
        existing = self.get_label_by_name(name)
        if existing:
            return existing
        return self.create_label(name)

    def create_label(self, name: str) -> Dict[str, Any]:
        """Create a new label (IMAP folder).

        Args:
            name: The label name.

        Returns:
            Created label dict with id and name.
        """
        conn = self._connect()
        status, data = conn.create(f'"{name}"')
        if status != "OK":
            raise ValueError(f"Failed to create label '{name}': {data}")
        return {"id": name, "name": name}

    def list_drafts(self, max_results: int = 10) -> List[Dict[str, Any]]:
        """List draft emails.

        Args:
            max_results: Maximum drafts to return.

        Returns:
            List of draft dicts with id.
        """
        conn = self._connect()
        self._select_folder("DRAFT", readonly=True)

        status, data = conn.uid("search", None, "ALL")
        if status != "OK":
            return []

        uids = data[0].split()
        if not uids:
            return []

        uids = list(reversed(uids))[:max_results]

        drafts = []
        for uid in uids:
            uid_str = uid.decode("ascii")
            drafts.append({
                "id": uid_str,
                "message": {"id": uid_str},
            })

        return drafts

    def get_mailbox_stats(self) -> Dict[str, Any]:
        """Get comprehensive mailbox statistics.

        Returns:
            Dictionary with profile, system_labels, categories, user_labels.
        """
        profile = self.get_profile()

        system_label_ids = [
            "INBOX", "SENT", "DRAFT", "SPAM", "TRASH",
            "STARRED", "IMPORTANT",
        ]

        system_labels = {}
        for label_id in system_label_ids:
            try:
                label = self.get_label(label_id)
                system_labels[label_id] = {
                    "total": label.get("messagesTotal", 0),
                    "unread": label.get("messagesUnread", 0),
                    "threads_total": label.get("threadsTotal", 0),
                    "threads_unread": label.get("threadsUnread", 0),
                }
            except (ValueError, imaplib.IMAP4.error) as e:
                logger.debug(f"Label {label_id} not accessible: {e}")

        # Get UNREAD count from INBOX unseen
        if "INBOX" in system_labels:
            system_labels["UNREAD"] = {
                "total": system_labels["INBOX"]["unread"],
                "unread": system_labels["INBOX"]["unread"],
                "threads_total": system_labels["INBOX"]["unread"],
                "threads_unread": system_labels["INBOX"]["unread"],
            }

        # Categories - use X-GM-RAW search in All Mail
        categories = {}
        category_names = {
            "Personal": "category:primary",
            "Updates": "category:updates",
            "Promotions": "category:promotions",
            "Social": "category:social",
            "Forums": "category:forums",
        }
        for cat_name, cat_query in category_names.items():
            try:
                conn = self._connect()
                self._select_folder("ALL", readonly=True)
                status, data = conn.uid("search", None, f'X-GM-RAW "{cat_query}"')
                if status == "OK":
                    uids = data[0].split()
                    categories[cat_name] = {
                        "id": f"CATEGORY_{cat_name.upper()}",
                        "total": len(uids),
                        "unread": 0,  # Can't easily get unread count per category via IMAP
                    }
            except (imaplib.IMAP4.error, OSError) as e:
                logger.debug(f"Category {cat_name} not accessible: {e}")

        # Get user labels
        all_labels = self.list_labels()
        user_labels = []
        for label in all_labels:
            if label.get("type") != "system":
                try:
                    details = self.get_label(label.get("id"))
                    user_labels.append({
                        "id": label.get("id"),
                        "name": label.get("name"),
                        "total": details.get("messagesTotal", 0),
                        "unread": details.get("messagesUnread", 0),
                    })
                except (ValueError, imaplib.IMAP4.error) as e:
                    logger.debug(f"User label {label.get('id')} not accessible: {e}")

        user_labels.sort(key=lambda x: x.get("unread", 0), reverse=True)

        # Calculate total messages
        total_messages = 0
        try:
            all_label = self.get_label("ALL")
            total_messages = all_label.get("messagesTotal", 0)
        except (ValueError, imaplib.IMAP4.error):
            pass

        return {
            "profile": {
                "email": profile.get("emailAddress"),
                "messages_total": total_messages,
                "threads_total": total_messages,
            },
            "system_labels": system_labels,
            "categories": categories,
            "user_labels": user_labels,
        }

    def create_draft(
        self,
        to: str,
        subject: str,
        body: str,
        cc: Optional[str] = None,
        html: bool = False,
    ) -> Dict[str, Any]:
        """Create a draft email by appending to the Drafts folder.

        Args:
            to: Recipient email address(es).
            subject: Email subject.
            body: Email body.
            cc: CC recipients.
            html: If True, body is HTML.

        Returns:
            Created draft response with id.
        """
        from email.mime.text import MIMEText
        import time

        msg = MIMEText(body, "html" if html else "plain")
        msg["To"] = to
        msg["From"] = self.email_address
        msg["Subject"] = subject
        if cc:
            msg["Cc"] = cc

        conn = self._connect()
        self._select_folder("DRAFT", readonly=False)

        # Append to Drafts folder with \\Draft flag
        status, data = conn.append(
            "[Gmail]/Drafts",
            "\\Draft",
            imaplib.Time2Internaldate(time.time()),
            msg.as_bytes(),
        )

        if status != "OK":
            raise ValueError(f"Failed to create draft: {data}")

        # Extract UID from APPENDUID response if available
        draft_id = "unknown"
        if data and data[0]:
            uid_match = re.search(rb"APPENDUID \d+ (\d+)", data[0])
            if uid_match:
                draft_id = uid_match.group(1).decode("ascii")

        return {"id": draft_id}

    def create_reply_draft(
        self,
        message_uid: str,
        body: str,
        reply_all: bool = False,
    ) -> Dict[str, Any]:
        """Create a draft reply to an existing message.

        Args:
            message_uid: The message UID to reply to.
            body: Reply body text.
            reply_all: If True, reply to all recipients.

        Returns:
            Created draft response.
        """
        from email.mime.text import MIMEText
        import time

        # Get original message
        original = self.get_message_details(message_uid)
        headers = original.get("headers", {})

        original_from = headers.get("from", "")
        original_subject = headers.get("subject", "")
        original_message_id = headers.get("message-id", "")
        original_references = headers.get("references", "")

        reply_to = original_from
        if reply_all:
            original_to = headers.get("to", "")
            all_recipients = [reply_to]
            if original_to:
                all_recipients.append(original_to)
            reply_to = ", ".join(all_recipients)

        if original_subject.lower().startswith("re:"):
            reply_subject = original_subject
        else:
            reply_subject = f"Re: {original_subject}"

        if original_references:
            references = f"{original_references} {original_message_id}"
        else:
            references = original_message_id

        msg = MIMEText(body, "plain")
        msg["To"] = reply_to
        msg["From"] = self.email_address
        msg["Subject"] = reply_subject
        if original_message_id:
            msg["In-Reply-To"] = original_message_id
            msg["References"] = references

        conn = self._connect()

        status, data = conn.append(
            "[Gmail]/Drafts",
            "\\Draft",
            imaplib.Time2Internaldate(time.time()),
            msg.as_bytes(),
        )

        if status != "OK":
            raise ValueError(f"Failed to create reply draft: {data}")

        draft_id = "unknown"
        if data and data[0]:
            uid_match = re.search(rb"APPENDUID \d+ (\d+)", data[0])
            if uid_match:
                draft_id = uid_match.group(1).decode("ascii")

        return {"id": draft_id}
