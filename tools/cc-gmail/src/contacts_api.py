"""Google People API (Contacts) wrapper for cc-gmail.

Requires OAuth authentication with contacts scope.
Uses google-api-python-client (already a dependency).
"""

import logging
from typing import Optional, List, Dict, Any

from googleapiclient.discovery import build
from google.oauth2.credentials import Credentials

logger = logging.getLogger(__name__)

PERSON_FIELDS = "names,emailAddresses,phoneNumbers,organizations,biographies"


class ContactsClient:
    """Google People API v1 client for contacts."""

    def __init__(self, credentials: Credentials):
        self.service = build("people", "v1", credentials=credentials)

    def list_contacts(self, max_results: int = 100) -> List[Dict[str, Any]]:
        """List contacts sorted by first name."""
        result = (
            self.service.people()
            .connections()
            .list(
                resourceName="people/me",
                pageSize=min(max_results, 1000),
                personFields=PERSON_FIELDS,
                sortOrder="FIRST_NAME_ASCENDING",
            )
            .execute()
        )

        return [
            self._format_contact(p) for p in result.get("connections", [])
        ]

    def search_contacts(self, query: str) -> List[Dict[str, Any]]:
        """Search contacts by name or email."""
        result = (
            self.service.people()
            .searchContacts(
                query=query,
                readMask=PERSON_FIELDS,
            )
            .execute()
        )

        contacts = []
        for item in result.get("results", []):
            person = item.get("person", {})
            contacts.append(self._format_contact(person))
        return contacts

    def get_contact(self, resource_name: str) -> Dict[str, Any]:
        """Get full details for a single contact."""
        person = (
            self.service.people()
            .get(
                resourceName=resource_name,
                personFields=PERSON_FIELDS,
            )
            .execute()
        )
        return self._format_contact(person)

    def create_contact(
        self,
        given_name: str,
        family_name: str = "",
        email: Optional[str] = None,
        phone: Optional[str] = None,
        organization: Optional[str] = None,
    ) -> Dict[str, Any]:
        """Create a new contact."""
        body: Dict[str, Any] = {
            "names": [{"givenName": given_name, "familyName": family_name}],
        }
        if email:
            body["emailAddresses"] = [{"value": email}]
        if phone:
            body["phoneNumbers"] = [{"value": phone}]
        if organization:
            body["organizations"] = [{"name": organization}]

        person = (
            self.service.people().createContact(body=body).execute()
        )
        return self._format_contact(person)

    def delete_contact(self, resource_name: str) -> None:
        """Delete a contact."""
        self.service.people().deleteContact(
            resourceName=resource_name
        ).execute()

    def _format_contact(self, person: dict) -> Dict[str, Any]:
        """Parse a People API person into a clean dict."""
        names = person.get("names", [{}])
        name_obj = names[0] if names else {}

        emails = [
            e.get("value", "") for e in person.get("emailAddresses", [])
        ]
        phones = [
            p.get("value", "") for p in person.get("phoneNumbers", [])
        ]

        orgs = person.get("organizations", [{}])
        org_obj = orgs[0] if orgs else {}

        return {
            "resource_name": person.get("resourceName", ""),
            "name": name_obj.get("displayName", ""),
            "given_name": name_obj.get("givenName", ""),
            "family_name": name_obj.get("familyName", ""),
            "emails": emails,
            "phones": phones,
            "organization": org_obj.get("name", ""),
            "title": org_obj.get("title", ""),
        }
