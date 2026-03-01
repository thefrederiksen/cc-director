"""
Vault Importer - Import documents and health data into the Vault

Handles importing various file formats into the Vault 2.0 platform:
- Word documents (.docx)
- PDF files (.pdf)
- Transcripts (markdown, JSON)
- Notes and journals
- Health data from MyHealthHelper
"""

import json
import logging
import shutil
from pathlib import Path
from datetime import datetime
from typing import Optional, List, Dict, Any

logger = logging.getLogger(__name__)

try:
    from .config import (
        VAULT_PATH, DOCUMENTS_PATH, HEALTH_PATH, IMPORTS_PATH,
        TRANSCRIPTS_PATH, NOTES_PATH, JOURNALS_PATH, RESEARCH_PATH,
        get_document_path
    )
    from .db import (
        add_document, update_document, get_document_by_path,
        add_health_entry, init_db
    )
except ImportError:
    from config import (
        VAULT_PATH, DOCUMENTS_PATH, HEALTH_PATH, IMPORTS_PATH,
        TRANSCRIPTS_PATH, NOTES_PATH, JOURNALS_PATH, RESEARCH_PATH,
        get_document_path
    )
    from db import (
        add_document, update_document, get_document_by_path,
        add_health_entry, init_db
    )


class VaultImporter:
    """Import documents and data into the Vault."""

    def __init__(self, index_vectors: bool = True, use_chunking: bool = True):
        """
        Initialize the importer.

        Args:
            index_vectors: Whether to index documents in the vector store
            use_chunking: Whether to use document chunking for better retrieval
        """
        self.index_vectors = index_vectors
        self.use_chunking = use_chunking
        self._vectors = None
        self._converters = None

        if index_vectors:
            try:
                from .vectors import get_vault_vectors
            except ImportError:
                from vectors import get_vault_vectors
            self._vectors = get_vault_vectors()

        # Load converters
        try:
            try:
                from .converters import convert_to_markdown, is_supported, get_file_type
            except ImportError:
                from converters import convert_to_markdown, is_supported, get_file_type
            self._converters = {
                'convert': convert_to_markdown,
                'is_supported': is_supported,
                'get_type': get_file_type
            }
        except ImportError:
            self._converters = None

    # ===========================================
    # UNIVERSAL FILE IMPORT
    # ===========================================

    def import_file(
        self,
        source_path: Path,
        doc_type: str = 'research',
        title: Optional[str] = None,
        tags: Optional[str] = None,
        contact_id: Optional[int] = None,
        goal_id: Optional[int] = None
    ) -> Dict[str, Any]:
        """
        Import any supported file type into the vault.

        Automatically converts Word docs, PDFs, and other formats to markdown.
        Stores the converted markdown in the vault and indexes for search.

        Supported formats: .docx, .pdf, .md, .txt

        Args:
            source_path: Path to the file to import
            doc_type: Document type (transcript, note, journal, research)
            title: Document title (defaults to filename)
            tags: Comma-separated tags
            contact_id: Link to a contact
            goal_id: Link to a goal

        Returns:
            Dict with success status, document_id, and metadata
        """
        source_path = Path(source_path)

        if not source_path.exists():
            return {'success': False, 'error': f"File not found: {source_path}"}

        if not self._converters:
            return {'success': False, 'error': "Converters not available. Install python-docx and pymupdf."}

        if not self._converters['is_supported'](source_path):
            return {
                'success': False,
                'error': f"Unsupported file type: {source_path.suffix}. Supported: .docx, .pdf, .md, .txt"
            }

        # Convert to markdown
        try:
            content, convert_meta = self._converters['convert'](source_path)
        except (OSError, ValueError, RuntimeError) as e:
            return {'success': False, 'error': f"Conversion failed: {e}"}

        # Generate filename
        date_prefix = datetime.now().strftime('%Y-%m-%d')
        base_name = (title or source_path.stem).replace(' ', '_').lower()
        # Clean filename
        base_name = ''.join(c for c in base_name if c.isalnum() or c in '_-')
        target_name = f"{date_prefix}_{base_name}.md"

        # Get target directory based on doc_type
        target_dir = get_document_path(doc_type)
        target_dir.mkdir(parents=True, exist_ok=True)
        target_file = target_dir / target_name

        # Handle duplicates
        counter = 1
        while target_file.exists():
            target_name = f"{date_prefix}_{base_name}_{counter}.md"
            target_file = target_dir / target_name
            counter += 1

        # Write converted content
        target_file.write_text(content, encoding='utf-8')

        # Calculate relative path for database
        relative_path = str(target_file.relative_to(DOCUMENTS_PATH))

        # Check if already exists in DB
        existing = get_document_by_path(relative_path)
        if existing:
            return {
                'success': False,
                'error': f"Document already exists: {relative_path}",
                'document_id': existing['id']
            }

        # Add to database
        init_db(silent=True)
        doc_id = add_document(
            path=relative_path,
            title=title or source_path.stem,
            doc_type=doc_type,
            source=str(source_path),  # Original file path
            tags=tags,
            contact_id=contact_id,
            goal_id=goal_id
        )

        result = {
            'success': True,
            'document_id': doc_id,
            'path': relative_path,
            'target_file': str(target_file),
            'source_file': str(source_path),
            'original_format': convert_meta.get('original_format'),
            'content_length': len(content)
        }

        # Add format-specific metadata
        if 'page_count' in convert_meta:
            result['page_count'] = convert_meta['page_count']

        # Index in vector store with chunking
        if self.index_vectors and self._vectors:
            try:
                if self.use_chunking:
                    chunk_ids = self._vectors.index_document_chunks(
                        document_id=doc_id,
                        content=content,
                        metadata={
                            'doc_type': doc_type,
                            'title': title or source_path.stem,
                            'original_format': convert_meta.get('original_format'),
                            'source': str(source_path)
                        }
                    )
                    result['chunk_count'] = len(chunk_ids)
                    if chunk_ids:
                        update_document(doc_id, vector_id=chunk_ids[0])
                else:
                    vector_id = self._vectors.add_document(
                        doc_id=f"doc_{doc_id}",
                        content=content,
                        metadata={
                            'doc_type': doc_type,
                            'title': title or source_path.stem
                        }
                    )
                    update_document(doc_id, vector_id=vector_id)
                    result['vector_id'] = vector_id
            except (OSError, ValueError, RuntimeError) as e:
                logger.warning(f"Vector indexing failed for doc {doc_id}: {e}")
                result['vector_warning'] = str(e)

        return result

    # ===========================================
    # TRANSCRIPT IMPORT
    # ===========================================

    def import_transcript(
        self,
        source_path: Path,
        title: Optional[str] = None,
        source: Optional[str] = None,
        source_date: Optional[str] = None,
        contact_id: Optional[int] = None,
        goal_id: Optional[int] = None,
        tags: Optional[str] = None
    ) -> Dict[str, Any]:
        """
        Import a transcript file into the vault.

        Expects either:
        - A markdown file (.md)
        - A JSON file with transcript data

        Returns import result with document ID and any warnings.
        """
        source_path = Path(source_path)

        if not source_path.exists():
            return {'success': False, 'error': f"File not found: {source_path}"}

        # Determine target filename
        if source_date:
            date_prefix = source_date
        else:
            date_prefix = datetime.now().strftime('%Y-%m-%d')

        # Generate unique filename
        base_name = title.replace(' ', '_').lower() if title else source_path.stem
        target_name = f"{date_prefix}_{base_name}"

        # Handle .md and .json files
        if source_path.suffix == '.md':
            target_file = TRANSCRIPTS_PATH / f"{target_name}.md"
            content = source_path.read_text(encoding='utf-8')
        elif source_path.suffix == '.json':
            target_file = TRANSCRIPTS_PATH / f"{target_name}.json"
            content = source_path.read_text(encoding='utf-8')
            # Try to extract text for indexing
            try:
                data = json.loads(content)
                if isinstance(data, list):
                    # Transcript segments
                    content = '\n'.join(seg.get('text', '') for seg in data)
                elif isinstance(data, dict):
                    content = data.get('text', data.get('transcript', str(data)))
            except json.JSONDecodeError:
                pass
        else:
            return {'success': False, 'error': f"Unsupported file type: {source_path.suffix}"}

        # Ensure target directory exists
        TRANSCRIPTS_PATH.mkdir(parents=True, exist_ok=True)

        # Copy file to vault
        shutil.copy2(source_path, target_file)

        # Calculate relative path for database
        relative_path = str(target_file.relative_to(DOCUMENTS_PATH))

        # Check if already exists
        existing = get_document_by_path(relative_path)
        if existing:
            return {
                'success': False,
                'error': f"Document already exists: {relative_path}",
                'document_id': existing['id']
            }

        # Add to database
        init_db(silent=True)
        doc_id = add_document(
            path=relative_path,
            title=title or source_path.stem,
            doc_type='transcript',
            source=source or str(source_path),
            source_date=source_date,
            contact_id=contact_id,
            goal_id=goal_id,
            tags=tags
        )

        result = {
            'success': True,
            'document_id': doc_id,
            'path': relative_path,
            'target_file': str(target_file)
        }

        # Index in vector store with chunking
        if self.index_vectors and self._vectors:
            try:
                if self.use_chunking:
                    # Use chunked indexing for better retrieval
                    chunk_ids = self._vectors.index_document_chunks(
                        document_id=doc_id,
                        content=content,
                        metadata={
                            'doc_type': 'transcript',
                            'source_date': source_date,
                            'title': title
                        }
                    )
                    result['chunk_count'] = len(chunk_ids)
                    result['vector_ids'] = chunk_ids
                    # Update document with first chunk vector ID for reference
                    if chunk_ids:
                        update_document(doc_id, vector_id=chunk_ids[0])
                else:
                    # Legacy: single vector for whole document
                    vector_id = self._vectors.add_document(
                        doc_id=f"doc_{doc_id}",
                        content=content,
                        metadata={
                            'doc_type': 'transcript',
                            'source_date': source_date,
                            'title': title
                        }
                    )
                    update_document(doc_id, vector_id=vector_id)
                    result['vector_id'] = vector_id
            except (RuntimeError, ValueError, OSError) as e:
                logger.warning(f"Vector indexing failed for doc {doc_id}: {e}")
                result['vector_warning'] = str(e)

        return result

    # ===========================================
    # NOTE/JOURNAL IMPORT
    # ===========================================

    def import_note(
        self,
        source_path: Path,
        doc_type: str = 'note',
        title: Optional[str] = None,
        tags: Optional[str] = None,
        goal_id: Optional[int] = None
    ) -> Dict[str, Any]:
        """
        Import a note or journal entry.

        doc_type should be 'note', 'journal', or 'research'.
        """
        source_path = Path(source_path)

        if not source_path.exists():
            return {'success': False, 'error': f"File not found: {source_path}"}

        if doc_type not in ('note', 'journal', 'research'):
            return {'success': False, 'error': f"Invalid doc_type: {doc_type}"}

        # Get target directory
        target_dir = get_document_path(doc_type)
        target_dir.mkdir(parents=True, exist_ok=True)

        # Generate filename with date
        date_prefix = datetime.now().strftime('%Y-%m-%d')
        target_name = f"{date_prefix}_{source_path.name}"
        target_file = target_dir / target_name

        # Read content
        content = source_path.read_text(encoding='utf-8')

        # Copy file
        shutil.copy2(source_path, target_file)

        # Calculate relative path
        relative_path = str(target_file.relative_to(DOCUMENTS_PATH))

        # Add to database
        init_db(silent=True)
        doc_id = add_document(
            path=relative_path,
            title=title or source_path.stem,
            doc_type=doc_type,
            source=str(source_path),
            goal_id=goal_id,
            tags=tags
        )

        result = {
            'success': True,
            'document_id': doc_id,
            'path': relative_path,
            'target_file': str(target_file)
        }

        # Index in vector store with chunking
        if self.index_vectors and self._vectors:
            try:
                if self.use_chunking:
                    # Use chunked indexing for better retrieval
                    chunk_ids = self._vectors.index_document_chunks(
                        document_id=doc_id,
                        content=content,
                        metadata={
                            'doc_type': doc_type,
                            'title': title
                        }
                    )
                    result['chunk_count'] = len(chunk_ids)
                    result['vector_ids'] = chunk_ids
                    if chunk_ids:
                        update_document(doc_id, vector_id=chunk_ids[0])
                else:
                    # Legacy: single vector for whole document
                    vector_id = self._vectors.add_document(
                        doc_id=f"doc_{doc_id}",
                        content=content,
                        metadata={
                            'doc_type': doc_type,
                            'title': title
                        }
                    )
                    update_document(doc_id, vector_id=vector_id)
                    result['vector_id'] = vector_id
            except (RuntimeError, ValueError, OSError) as e:
                logger.warning(f"Vector indexing failed for doc {doc_id}: {e}")
                result['vector_warning'] = str(e)

        return result

    # ===========================================
    # HEALTH DATA IMPORT
    # ===========================================

    def import_health_data(
        self,
        source_dir: Path,
        category: Optional[str] = None
    ) -> Dict[str, Any]:
        """
        Import health data from MyHealthHelper format.

        Expected structure:
        source_dir/
            daily/
                2026-02-18.json
            sleep/
                2026-02-18.json
            workouts/
                2026-02-18.json

        Returns summary of imported entries.
        """
        source_dir = Path(source_dir)

        if not source_dir.exists():
            return {'success': False, 'error': f"Directory not found: {source_dir}"}

        results = {
            'success': True,
            'imported': 0,
            'skipped': 0,
            'errors': [],
            'by_category': {}
        }

        init_db(silent=True)

        # Determine categories to process
        if category:
            categories = [category]
        else:
            # Auto-detect categories from subdirectories
            categories = [d.name for d in source_dir.iterdir() if d.is_dir()]

        for cat in categories:
            cat_dir = source_dir / cat
            if not cat_dir.exists():
                continue

            results['by_category'][cat] = {'imported': 0, 'skipped': 0}
            target_cat_dir = HEALTH_PATH / cat
            target_cat_dir.mkdir(parents=True, exist_ok=True)

            for json_file in cat_dir.glob('*.json'):
                try:
                    # Parse date from filename
                    date_str = json_file.stem  # e.g., "2026-02-18"

                    # Copy file to vault
                    target_file = target_cat_dir / json_file.name
                    if target_file.exists():
                        results['skipped'] += 1
                        results['by_category'][cat]['skipped'] += 1
                        continue

                    shutil.copy2(json_file, target_file)

                    # Read and generate summary
                    data = json.loads(json_file.read_text(encoding='utf-8'))
                    summary = self._generate_health_summary(cat, data)

                    # Add to database
                    relative_path = str(target_file.relative_to(HEALTH_PATH))
                    entry_id = add_health_entry(
                        category=cat,
                        entry_date=date_str,
                        data_file=relative_path,
                        summary=summary
                    )

                    results['imported'] += 1
                    results['by_category'][cat]['imported'] += 1

                    # Index in vector store
                    if self.index_vectors and self._vectors and summary:
                        try:
                            self._vectors.add_health_entry(
                                entry_id=f"health_{entry_id}",
                                summary=summary,
                                metadata={
                                    'category': cat,
                                    'date': date_str
                                }
                            )
                        except (RuntimeError, ValueError, OSError) as e:
                            logger.debug(f"Health vector indexing skipped: {e}")

                except (json.JSONDecodeError, IOError, OSError) as e:
                    results['errors'].append(f"{json_file.name}: {str(e)}")

        return results

    def _generate_health_summary(self, category: str, data: Dict[str, Any]) -> str:
        """Generate a text summary of health data for indexing."""
        if category == 'daily':
            parts = []
            if 'weight' in data:
                parts.append(f"Weight: {data['weight']} lbs")
            if 'steps' in data:
                parts.append(f"Steps: {data['steps']}")
            if 'calories' in data:
                parts.append(f"Calories: {data['calories']}")
            if 'mood' in data:
                parts.append(f"Mood: {data['mood']}")
            if 'notes' in data:
                parts.append(f"Notes: {data['notes']}")
            return '; '.join(parts) if parts else None

        elif category == 'sleep':
            parts = []
            if 'hours' in data:
                parts.append(f"Sleep: {data['hours']} hours")
            if 'quality' in data:
                parts.append(f"Quality: {data['quality']}")
            if 'bedtime' in data:
                parts.append(f"Bedtime: {data['bedtime']}")
            if 'waketime' in data:
                parts.append(f"Wake: {data['waketime']}")
            return '; '.join(parts) if parts else None

        elif category == 'workouts':
            parts = []
            if 'type' in data:
                parts.append(f"Workout: {data['type']}")
            if 'duration' in data:
                parts.append(f"Duration: {data['duration']} min")
            if 'intensity' in data:
                parts.append(f"Intensity: {data['intensity']}")
            if 'exercises' in data:
                parts.append(f"Exercises: {', '.join(data['exercises'])}")
            return '; '.join(parts) if parts else None

        else:
            # Generic summary
            return json.dumps(data)[:500]

    # ===========================================
    # BULK IMPORT
    # ===========================================

    def import_from_staging(self) -> Dict[str, Any]:
        """
        Import all files from the vault imports staging directory.

        Files should be organized by type:
        imports/
            transcripts/
                file.md
            notes/
                file.md
            health/
                daily/
                    2026-02-18.json
        """
        results = {
            'transcripts': [],
            'notes': [],
            'journals': [],
            'research': [],
            'health': None
        }

        # Import transcripts
        transcripts_staging = IMPORTS_PATH / 'transcripts'
        if transcripts_staging.exists():
            for f in transcripts_staging.glob('*'):
                if f.is_file():
                    result = self.import_transcript(f)
                    results['transcripts'].append({
                        'file': f.name,
                        **result
                    })
                    if result['success']:
                        f.unlink()  # Remove from staging

        # Import notes
        for doc_type in ['notes', 'journals', 'research']:
            staging_dir = IMPORTS_PATH / doc_type
            if staging_dir.exists():
                for f in staging_dir.glob('*'):
                    if f.is_file():
                        result = self.import_note(f, doc_type=doc_type.rstrip('s'))
                        results[doc_type].append({
                            'file': f.name,
                            **result
                        })
                        if result['success']:
                            f.unlink()

        # Import health data
        health_staging = IMPORTS_PATH / 'health'
        if health_staging.exists():
            results['health'] = self.import_health_data(health_staging)

        return results


def import_document(
    path: str,
    doc_type: str = 'note',
    title: Optional[str] = None,
    tags: Optional[str] = None,
    index: bool = True
) -> Dict[str, Any]:
    """Convenience function for importing a single document."""
    importer = VaultImporter(index_vectors=index)
    source_path = Path(path)

    # Use universal import for Word/PDF, legacy methods for md/json
    ext = source_path.suffix.lower()
    if ext in ('.docx', '.pdf', '.txt'):
        return importer.import_file(source_path, doc_type=doc_type, title=title, tags=tags)
    elif doc_type == 'transcript':
        return importer.import_transcript(source_path, title=title, tags=tags)
    else:
        return importer.import_note(source_path, doc_type=doc_type, title=title, tags=tags)
