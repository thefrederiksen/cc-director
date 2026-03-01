"""
Vault RAG - Retrieval Augmented Generation for the Vault

Provides semantic search and RAG query capabilities across all vault data.
Uses SQLite-native vector storage and OpenAI for answer synthesis.
"""

import logging
from typing import List, Dict, Any, Optional

try:
    import openai
except ImportError:
    raise ImportError(
        "openai is required for RAG functionality.\n"
        "Install with: pip install openai"
    )

try:
    from .config import OPENAI_API_KEY, HYBRID_VECTOR_WEIGHT, HYBRID_TEXT_WEIGHT
except ImportError:
    from config import OPENAI_API_KEY, HYBRID_VECTOR_WEIGHT, HYBRID_TEXT_WEIGHT

logger = logging.getLogger(__name__)


class VaultRAG:
    """RAG query engine for the Vault."""

    def __init__(self):
        """Initialize the RAG engine."""
        self._openai_client = None

        # Initialize vector store (SQLite-backed, always available)
        try:
            from .vectors import get_vault_vectors
        except ImportError:
            from vectors import get_vault_vectors
        self._vectors = get_vault_vectors()

        # Initialize OpenAI
        if OPENAI_API_KEY:
            self._openai_client = openai.OpenAI(
                api_key=OPENAI_API_KEY,
                timeout=30.0
            )
        else:
            logger.warning("OPENAI_API_KEY not set - RAG queries will not work")

    def semantic_search(
        self,
        query: str,
        collections: Optional[List[str]] = None,
        n_results: int = 10
    ) -> Dict[str, List[Dict[str, Any]]]:
        """
        Perform semantic search across vault collections.

        Args:
            query: Search query
            collections: List of collections to search (default: all)
            n_results: Number of results per collection

        Returns:
            Dict mapping collection names to lists of results
        """
        if not self._vectors:
            return {'error': 'Vector store not available'}

        return self._vectors.semantic_search(query, collections, n_results)

    def hybrid_search(
        self,
        query: str,
        n_results: int = 10,
        vector_weight: Optional[float] = None,
        text_weight: Optional[float] = None
    ) -> List[Dict[str, Any]]:
        """
        Perform hybrid search combining vector similarity and BM25 keyword search.

        Args:
            query: Search query
            n_results: Number of results to return
            vector_weight: Weight for semantic similarity (default from config)
            text_weight: Weight for keyword matching (default from config)

        Returns:
            List of chunk results with combined scores and citations
        """
        if not self._vectors:
            return []

        vector_weight = vector_weight or HYBRID_VECTOR_WEIGHT
        text_weight = text_weight or HYBRID_TEXT_WEIGHT

        return self._vectors.hybrid_search(
            query=query,
            n_results=n_results,
            vector_weight=vector_weight,
            text_weight=text_weight
        )

    def ask(
        self,
        question: str,
        collections: Optional[List[str]] = None,
        n_context: int = 5,
        model: str = "gpt-4o",
        use_hybrid: bool = True
    ) -> Dict[str, Any]:
        """
        Ask a question and get an answer using RAG.

        Retrieves relevant context from the vault and uses an LLM to synthesize an answer.

        Args:
            question: The question to answer
            collections: Collections to search for context
            n_context: Number of context items to retrieve
            model: OpenAI model to use
            use_hybrid: Use hybrid search (vector + BM25) for document chunks

        Returns:
            Dict with answer, sources, and metadata
        """
        if not self._vectors:
            return {'error': 'Vector store not available'}

        if not self._openai_client:
            return {'error': 'OpenAI client not available. Set OPENAI_API_KEY.'}

        context_parts = []
        sources = []

        if use_hybrid:
            # Use hybrid search for document chunks (best for documents)
            chunk_results = self.hybrid_search(question, n_results=n_context)

            for chunk in chunk_results:
                content = chunk.get('content', '')
                if content:
                    meta = chunk.get('metadata', {})
                    doc_title = meta.get('doc_title', meta.get('title', 'Document'))
                    start_line = meta.get('start_line', 1)
                    end_line = meta.get('end_line', 1)

                    # Format with citation
                    citation = f"[{doc_title}, lines {start_line}-{end_line}]"
                    context_parts.append(f"{citation}\n{content[:1500]}")

                    sources.append({
                        'type': 'chunk',
                        'chunk_id': chunk.get('chunk_id'),
                        'doc_title': doc_title,
                        'doc_path': meta.get('doc_path'),
                        'lines': f"{start_line}-{end_line}",
                        'vector_score': chunk.get('vector_score'),
                        'bm25_score': chunk.get('bm25_score'),
                        'combined_score': chunk.get('combined_score')
                    })

            # Also search other collections (facts, ideas, health) with semantic search
            other_collections = [c for c in (collections or ['facts', 'ideas', 'health'])
                                if c not in ['documents', 'chunks']]
            if other_collections:
                other_results = self._vectors.semantic_search(question, other_collections, n_context // 2)

                for coll_name, items in other_results.items():
                    for item in items:
                        if item.get('document'):
                            context_parts.append(f"[{coll_name.upper()}] {item['document'][:1000]}")
                            sources.append({
                                'type': coll_name,
                                'id': item['id'],
                                'distance': item.get('distance'),
                                'metadata': item.get('metadata', {})
                            })
        else:
            # Legacy: pure semantic search across all collections
            search_results = self._vectors.semantic_search(question, collections, n_context)

            for coll_name, items in search_results.items():
                for item in items:
                    if item.get('document'):
                        context_parts.append(f"[{coll_name.upper()}] {item['document'][:1000]}")
                        sources.append({
                            'type': coll_name,
                            'id': item['id'],
                            'distance': item.get('distance'),
                            'metadata': item.get('metadata', {})
                        })

        if not context_parts:
            return {
                'answer': "No relevant information found in the vault.",
                'sources': [],
                'context_used': 0
            }

        context = '\n\n'.join(context_parts)

        # Build prompt
        system_prompt = """You are a helpful assistant that answers questions based on the user's personal vault data.
Use the provided context to answer questions accurately. If the context doesn't contain relevant information, say so.
Keep answers concise and factual. When referencing information, include the source citation shown in brackets."""

        user_prompt = f"""Context from my personal vault:
{context}

Question: {question}

Answer based on the context above, citing your sources:"""

        # Get answer from LLM
        try:
            response = self._openai_client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt}
                ],
                max_tokens=1000,
                temperature=0.3
            )
        except (openai.APIError, openai.APIConnectionError, openai.APITimeoutError) as e:
            return {'error': f'OpenAI API error: {e}'}

        if not response.choices:
            return {'error': 'OpenAI returned empty response'}

        answer = response.choices[0].message.content

        return {
            'answer': answer,
            'sources': sources,
            'context_used': len(context_parts),
            'model': model,
            'tokens': response.usage.total_tokens if response.usage else None,
            'search_mode': 'hybrid' if use_hybrid else 'semantic'
        }

    def summarize(
        self,
        topic: str,
        collections: Optional[List[str]] = None,
        n_context: int = 10,
        model: str = "gpt-4o"
    ) -> Dict[str, Any]:
        """
        Summarize information about a topic from the vault.

        Args:
            topic: Topic to summarize
            collections: Collections to search
            n_context: Number of context items
            model: OpenAI model to use

        Returns:
            Dict with summary, sources, and metadata
        """
        if not self._vectors:
            return {'error': 'Vector store not available'}

        if not self._openai_client:
            return {'error': 'OpenAI client not available. Set OPENAI_API_KEY.'}

        # Retrieve relevant context
        search_results = self._vectors.semantic_search(topic, collections, n_context)

        # Assemble context
        context_parts = []
        sources = []

        for coll_name, items in search_results.items():
            for item in items:
                if item.get('document'):
                    context_parts.append(f"[{coll_name.upper()}] {item['document'][:1000]}")
                    sources.append({
                        'collection': coll_name,
                        'id': item['id'],
                        'metadata': item.get('metadata', {})
                    })

        if not context_parts:
            return {
                'summary': f"No information found about '{topic}' in the vault.",
                'sources': [],
                'context_used': 0
            }

        context = '\n\n'.join(context_parts)

        # Build prompt
        system_prompt = """You are a helpful assistant that summarizes information from the user's personal vault.
Create clear, concise summaries that capture the key points. Organize information logically.
Mention patterns, trends, or connections you notice across different pieces of information."""

        user_prompt = f"""Topic: {topic}

Information from my vault:
{context}

Please provide a comprehensive summary of what I have recorded about this topic:"""

        try:
            response = self._openai_client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt}
                ],
                max_tokens=1500,
                temperature=0.3
            )
        except (openai.APIError, openai.APIConnectionError, openai.APITimeoutError) as e:
            return {'error': f'OpenAI API error: {e}'}

        if not response.choices:
            return {'error': 'OpenAI returned empty response'}

        summary = response.choices[0].message.content

        return {
            'summary': summary,
            'sources': sources,
            'context_used': len(context_parts),
            'model': model,
            'tokens': response.usage.total_tokens if response.usage else None
        }

    def find_similar(
        self,
        entity_type: str,
        entity_id: int,
        n_results: int = 5
    ) -> List[Dict[str, Any]]:
        """
        Find similar items to a given entity.

        Args:
            entity_type: Type of entity (idea, document, fact)
            entity_id: ID of the entity
            n_results: Number of similar items to return

        Returns:
            List of similar items
        """
        if not self._vectors:
            return []

        vector_id = f"{entity_type}_{entity_id}"

        if entity_type == 'idea':
            return self._vectors.find_similar_ideas(vector_id, n_results)
        else:
            # Generic similarity search by getting the entity and re-querying
            # This is a simplified implementation
            return []

    def health_insights(
        self,
        query: str = "recent health trends",
        days: int = 30,
        model: str = "gpt-4o"
    ) -> Dict[str, Any]:
        """
        Get insights about health data.

        Args:
            query: Health-related query
            days: Number of days to consider
            model: OpenAI model to use

        Returns:
            Dict with insights, data summary, and recommendations
        """
        if not self._vectors:
            return {'error': 'Vector store not available'}

        if not self._openai_client:
            return {'error': 'OpenAI client not available. Set OPENAI_API_KEY.'}

        # Search health collection
        health_results = self._vectors.query_health(query, n_results=20)

        if not health_results:
            return {
                'insights': "No health data found in the vault.",
                'data_points': 0
            }

        # Assemble health data
        health_summaries = []
        for item in health_results:
            if item.get('document'):
                meta = item.get('metadata', {})
                date_str = meta.get('date', 'unknown date')
                category = meta.get('category', 'health')
                health_summaries.append(f"[{date_str}] ({category}) {item['document']}")

        context = '\n'.join(health_summaries)

        system_prompt = """You are a health data analyst reviewing personal health tracking data.
Identify trends, patterns, and notable observations. Be factual and avoid medical advice.
Focus on what the data shows and suggest areas that might warrant attention or tracking."""

        user_prompt = f"""Health tracking data:
{context}

Query: {query}

Please provide insights based on this health data:"""

        try:
            response = self._openai_client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt}
                ],
                max_tokens=1000,
                temperature=0.3
            )
        except (openai.APIError, openai.APIConnectionError, openai.APITimeoutError) as e:
            return {'error': f'OpenAI API error: {e}'}

        if not response.choices:
            return {'error': 'OpenAI returned empty response'}

        return {
            'insights': response.choices[0].message.content,
            'data_points': len(health_results),
            'model': model,
            'tokens': response.usage.total_tokens if response.usage else None
        }


# Singleton instance
_vault_rag = None


def get_vault_rag() -> VaultRAG:
    """Get the singleton VaultRAG instance."""
    global _vault_rag
    if _vault_rag is None:
        _vault_rag = VaultRAG()
    return _vault_rag


# Convenience functions
def ask(question: str, **kwargs) -> Dict[str, Any]:
    """Ask a question to the vault."""
    return get_vault_rag().ask(question, **kwargs)


def search(query: str, **kwargs) -> Dict[str, List[Dict[str, Any]]]:
    """Semantic search across the vault."""
    return get_vault_rag().semantic_search(query, **kwargs)


def hybrid_search(query: str, **kwargs) -> List[Dict[str, Any]]:
    """Hybrid search (vector + BM25) across document chunks."""
    return get_vault_rag().hybrid_search(query, **kwargs)


def summarize(topic: str, **kwargs) -> Dict[str, Any]:
    """Summarize information about a topic."""
    return get_vault_rag().summarize(topic, **kwargs)
