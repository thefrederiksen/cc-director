"""
Vault Graph Module

Provides graph traversal and query capabilities for the entity relationship graph.
Agents can use this to discover relationships between vault items.
"""

from collections import deque
from typing import Dict, List, Optional, Any, Protocol


class DbModule(Protocol):
    """Protocol for database module interface."""
    def get_entity_links(self, entity_type: str, entity_id: int) -> Dict: ...
    def get_graph_stats(self) -> Dict: ...
    def get_contact_by_id(self, contact_id: int) -> Optional[Dict]: ...
    def get_task(self, task_id: int) -> Optional[Dict]: ...
    def get_goal(self, goal_id: int) -> Optional[Dict]: ...
    def get_idea(self, idea_id: int) -> Optional[Dict]: ...
    def get_document(self, doc_id: int) -> Optional[Dict]: ...


class VaultGraph:
    """Graph traversal and query operations for Vault entities."""

    def __init__(self, db_module: DbModule) -> None:
        """Initialize with a database module reference."""
        self.db: DbModule = db_module

    def get_links(self, entity_type: str, entity_id: int, depth: int = 1) -> Dict:
        """
        Get all entities linked to the given entity.

        Args:
            entity_type: Type of entity (contact, task, goal, etc.)
            entity_id: ID of the entity
            depth: How many levels of connections to traverse (1=direct only)

        Returns:
            Dict with entity info and list of linked items
        """
        # Get the entity details first
        entity = self._get_entity_details(entity_type, entity_id)
        if not entity:
            return {"error": f"{entity_type}:{entity_id} not found"}

        # Get direct links
        links_data = self.db.get_entity_links(entity_type, entity_id)

        links = []

        # Process outgoing links
        for link in links_data.get('outgoing', []):
            target_entity = self._get_entity_details(link['target_type'], link['target_id'])
            if target_entity:
                links.append({
                    "type": link['target_type'],
                    "id": link['target_id'],
                    "label": target_entity.get('label', f"#{link['target_id']}"),
                    "relationship": link.get('relationship'),
                    "strength": link.get('strength', 1),
                    "direction": "outgoing"
                })

        # Process incoming links
        for link in links_data.get('incoming', []):
            source_entity = self._get_entity_details(link['source_type'], link['source_id'])
            if source_entity:
                links.append({
                    "type": link['source_type'],
                    "id": link['source_id'],
                    "label": source_entity.get('label', f"#{link['source_id']}"),
                    "relationship": link.get('relationship'),
                    "strength": link.get('strength', 1),
                    "direction": "incoming"
                })

        # If depth > 1, traverse further
        if depth > 1:
            seen = {(entity_type, entity_id)}
            current_links = links[:]

            for _ in range(depth - 1):
                next_level = []
                for link in current_links:
                    key = (link['type'], link['id'])
                    if key not in seen:
                        seen.add(key)
                        nested = self.db.get_entity_links(link['type'], link['id'])
                        for nested_link in nested.get('outgoing', []) + nested.get('incoming', []):
                            target_type = nested_link.get('target_type') or nested_link.get('source_type')
                            target_id = nested_link.get('target_id') or nested_link.get('source_id')
                            if (target_type, target_id) not in seen:
                                target_entity = self._get_entity_details(target_type, target_id)
                                if target_entity:
                                    next_level.append({
                                        "type": target_type,
                                        "id": target_id,
                                        "label": target_entity.get('label', f"#{target_id}"),
                                        "relationship": nested_link.get('relationship'),
                                        "strength": nested_link.get('strength', 1),
                                        "direction": "outgoing" if 'target_type' in nested_link else "incoming",
                                        "via": f"{link['type']}:{link['id']}"
                                    })
                links.extend(next_level)
                current_links = next_level

        return {
            "entity": {
                "type": entity_type,
                "id": entity_id,
                "label": entity.get('label', f"#{entity_id}")
            },
            "links": links,
            "total_links": len(links)
        }

    def get_stats(self) -> Dict:
        """
        Get graph statistics - entity counts and link totals.

        Returns:
            Dict with entity counts by type, total links, and most connected entities
        """
        return self.db.get_graph_stats()

    def find_path(
        self,
        from_type: str,
        from_id: int,
        to_type: str,
        to_id: int,
        max_depth: int = 5
    ) -> Optional[List[Dict]]:
        """
        Find shortest path between two entities using BFS.

        Args:
            from_type: Source entity type
            from_id: Source entity ID
            to_type: Target entity type
            to_id: Target entity ID
            max_depth: Maximum path length to search

        Returns:
            List of entities in the path, or None if no path found
        """
        start = (from_type, from_id)
        end = (to_type, to_id)

        if start == end:
            entity = self._get_entity_details(from_type, from_id)
            return [{"type": from_type, "id": from_id, "label": entity.get('label', f"#{from_id}")}]

        # BFS
        queue = deque([(start, [start])])
        visited = {start}

        while queue and len(visited) < 1000:  # Safety limit
            (current_type, current_id), path = queue.popleft()

            if len(path) > max_depth:
                continue

            # Get neighbors
            links = self.db.get_entity_links(current_type, current_id)
            neighbors = []

            for link in links.get('outgoing', []):
                neighbors.append((link['target_type'], link['target_id'], link.get('relationship')))
            for link in links.get('incoming', []):
                neighbors.append((link['source_type'], link['source_id'], link.get('relationship')))

            for neighbor_type, neighbor_id, relationship in neighbors:
                neighbor = (neighbor_type, neighbor_id)

                if neighbor == end:
                    # Found path
                    full_path = path + [neighbor]
                    result = []
                    for i, (etype, eid) in enumerate(full_path):
                        entity = self._get_entity_details(etype, eid)
                        entry = {
                            "type": etype,
                            "id": eid,
                            "label": entity.get('label', f"#{eid}") if entity else f"#{eid}"
                        }
                        if i > 0:
                            entry["relationship"] = relationship
                        result.append(entry)
                    return result

                if neighbor not in visited:
                    visited.add(neighbor)
                    queue.append((neighbor, path + [neighbor]))

        return None  # No path found

    def get_context(self, entity_type: str, entity_id: int) -> Dict:
        """
        Get entity with all linked entities expanded.
        This is the main method agents should use to get full context.

        Args:
            entity_type: Type of entity
            entity_id: ID of the entity

        Returns:
            Dict with entity details and all linked items with their details
        """
        # Get base entity
        entity = self._get_entity_details(entity_type, entity_id)
        if not entity:
            return {"error": f"{entity_type}:{entity_id} not found"}

        # Get all links
        links_data = self.db.get_entity_links(entity_type, entity_id)

        # Expand linked entities
        linked_items = []

        for link in links_data.get('outgoing', []):
            linked = self._get_entity_details(link['target_type'], link['target_id'])
            if linked:
                linked_items.append({
                    "type": link['target_type'],
                    "id": link['target_id'],
                    "relationship": link.get('relationship'),
                    "strength": link.get('strength', 1),
                    "direction": "outgoing",
                    "details": linked
                })

        for link in links_data.get('incoming', []):
            linked = self._get_entity_details(link['source_type'], link['source_id'])
            if linked:
                linked_items.append({
                    "type": link['source_type'],
                    "id": link['source_id'],
                    "relationship": link.get('relationship'),
                    "strength": link.get('strength', 1),
                    "direction": "incoming",
                    "details": linked
                })

        return {
            "entity": {
                "type": entity_type,
                "id": entity_id,
                "details": entity
            },
            "linked": linked_items,
            "total_linked": len(linked_items)
        }

    def _get_entity_details(self, entity_type: str, entity_id: int) -> Optional[Dict]:
        """Get details for an entity based on its type."""
        try:
            if entity_type == "contact":
                contact = self.db.get_contact_by_id(entity_id)
                if contact:
                    return {
                        "label": contact.get('name', f"#{entity_id}"),
                        "name": contact.get('name'),
                        "email": contact.get('email'),
                        "company": contact.get('company'),
                        "role": contact.get('title'),
                    }

            elif entity_type == "task":
                task = self.db.get_task(entity_id)
                if task:
                    return {
                        "label": task.get('title', f"#{entity_id}"),
                        "title": task.get('title'),
                        "status": task.get('status'),
                        "due_date": task.get('due_date'),
                        "priority": task.get('priority'),
                    }

            elif entity_type == "goal":
                goal = self.db.get_goal(entity_id)
                if goal:
                    return {
                        "label": goal.get('title', f"#{entity_id}"),
                        "title": goal.get('title'),
                        "status": goal.get('status'),
                        "progress": goal.get('progress'),
                        "target_date": goal.get('target_date'),
                    }

            elif entity_type == "idea":
                idea = self.db.get_idea(entity_id)
                if idea:
                    content = idea.get('content', '')
                    return {
                        "label": content[:50] + "..." if len(content) > 50 else content,
                        "content": content,
                        "status": idea.get('status'),
                        "domain": idea.get('domain'),
                    }

            elif entity_type == "document":
                doc = self.db.get_document(entity_id)
                if doc:
                    return {
                        "label": doc.get('title', f"#{entity_id}"),
                        "title": doc.get('title'),
                        "doc_type": doc.get('doc_type'),
                        "path": doc.get('path'),
                        "tags": doc.get('tags'),
                    }

            # Generic fallback for other types
            return {"label": f"{entity_type}:{entity_id}"}

        except (KeyError, AttributeError, TypeError):
            return {"label": f"{entity_type}:{entity_id}"}


def get_vault_graph() -> VaultGraph:
    """Get a VaultGraph instance with the database module."""
    try:
        from . import db
    except ImportError:
        import db
    db.init_db(silent=True)
    return VaultGraph(db)
