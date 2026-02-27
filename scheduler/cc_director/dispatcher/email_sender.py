"""Email sender using cc-outlook and cc-gmail CLI tools."""

import asyncio
import logging
import re
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

from .config import SEND_FROM_ACCOUNTS

# Regex to match URLs that are not already in an href
URL_PATTERN = re.compile(
    r'(?<!href=["\'])(?<!href=)'  # Not already in an href
    r'(https?://[^\s<>"\']+)',     # Match http/https URLs
    re.IGNORECASE
)

logger = logging.getLogger("cc_director.dispatcher.email")


@dataclass
class SendResult:
    """Result of a send operation."""

    success: bool
    message: str
    stdout: Optional[str] = None
    stderr: Optional[str] = None


class EmailSender:
    """Send emails via cc-outlook (Microsoft 365) or cc-gmail (Google)."""

    def __init__(self):
        """Initialize the email sender."""
        self.accounts = SEND_FROM_ACCOUNTS

    def _linkify_urls(self, content: str) -> str:
        """Convert plain URLs in content to clickable HTML links.

        Args:
            content: HTML content that may contain plain URLs

        Returns:
            Content with URLs wrapped in <a> tags
        """
        def replace_url(match):
            url = match.group(1)
            return f'<a href="{url}">{url}</a>'

        return URL_PATTERN.sub(replace_url, content)

    async def send(self, item: Dict[str, Any]) -> SendResult:
        """
        Send email using appropriate tool based on send_from account.

        Args:
            item: Content item dictionary with email_specific, send_from, etc.

        Returns:
            SendResult indicating success/failure
        """
        email_specific = item.get("email_specific")
        if not email_specific:
            return SendResult(
                success=False,
                message="Missing email_specific field"
            )

        to_list = email_specific.get("to", [])
        if not to_list:
            return SendResult(
                success=False,
                message="Missing email recipient"
            )

        # Get the primary recipient
        to_email = to_list[0] if isinstance(to_list, list) else to_list

        # Get subject
        subject = email_specific.get("subject", "(No subject)")

        # Get content and linkify URLs
        content = item.get("content", "")
        if not content:
            return SendResult(
                success=False,
                message="Missing email content"
            )
        content = self._linkify_urls(content)

        # Get account config - try send_from first, then persona
        account_key = item.get("send_from") or item.get("persona", "mindzie")
        # Map persona names to account names
        if account_key == "center_consulting":
            account_key = "consulting"
        account = self.accounts.get(account_key)
        if not account:
            return SendResult(
                success=False,
                message=f"Unknown account: {account_key}"
            )

        # Build command based on tool
        if account["tool"] == "cc-outlook":
            cmd = self._build_outlook_command(to_email, subject, content, email_specific)
        else:  # cc-gmail
            cmd = self._build_gmail_command(
                account["tool_account"],
                to_email,
                subject,
                content,
                email_specific
            )

        # Execute the command
        try:
            logger.info(f"Sending email from {account['email']} to {to_email}")
            logger.debug(f"Command: {' '.join(cmd)}")

            result = await asyncio.create_subprocess_exec(
                *cmd,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE
            )
            stdout, stderr = await result.communicate()

            stdout_str = stdout.decode() if stdout else ""
            stderr_str = stderr.decode() if stderr else ""

            if result.returncode == 0:
                logger.info(f"Email sent successfully from {account['email']} to {to_email}")
                return SendResult(
                    success=True,
                    message=f"Email sent from {account['email']} to {to_email}",
                    stdout=stdout_str,
                    stderr=stderr_str
                )
            else:
                logger.error(f"Email send failed: {stderr_str}")
                return SendResult(
                    success=False,
                    message=f"Send failed with exit code {result.returncode}",
                    stdout=stdout_str,
                    stderr=stderr_str
                )

        except FileNotFoundError as e:
            error_msg = f"Email tool not found: {cmd[0]}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
        except Exception as e:
            error_msg = f"Error sending email: {e}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )

    def _build_outlook_command(
        self,
        to_email: str,
        subject: str,
        body: str,
        email_specific: Dict[str, Any]
    ) -> List[str]:
        """Build cc-outlook send command."""
        cmd = [
            r"C:\cc-tools\cc-outlook.exe", "send",
            "-t", to_email,
            "-s", subject,
            "-b", body,
        ]

        # Add CC if present
        cc_list = email_specific.get("cc", [])
        for cc in cc_list:
            cmd.extend(["--cc", cc])

        # Add BCC if present
        bcc_list = email_specific.get("bcc", [])
        for bcc in bcc_list:
            cmd.extend(["--bcc", bcc])

        # Add attachments if present
        attachments = email_specific.get("attachments", [])
        for attachment in attachments:
            cmd.extend(["--attach", attachment])

        return cmd

    def _build_gmail_command(
        self,
        account: str,
        to_email: str,
        subject: str,
        body: str,
        email_specific: Dict[str, Any]
    ) -> List[str]:
        """Build cc-gmail send command."""
        cmd = [
            r"C:\cc-tools\cc-gmail.exe",
            "-a", account,
            "send",
            "-t", to_email,
            "-s", subject,
            "-b", body,
        ]

        # Add CC if present
        cc_list = email_specific.get("cc", [])
        for cc in cc_list:
            cmd.extend(["--cc", cc])

        # Add BCC if present
        bcc_list = email_specific.get("bcc", [])
        for bcc in bcc_list:
            cmd.extend(["--bcc", bcc])

        # Add attachments if present
        attachments = email_specific.get("attachments", [])
        for attachment in attachments:
            cmd.extend(["--attach", attachment])

        return cmd
