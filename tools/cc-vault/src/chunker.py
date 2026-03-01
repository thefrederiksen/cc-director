"""
Vault Chunker - Document chunking for better vector search

Splits documents into overlapping chunks for improved retrieval quality.
Uses tiktoken for accurate token counting.
"""

import hashlib
from dataclasses import dataclass
from typing import List, Optional

try:
    import tiktoken
except ImportError:
    raise ImportError(
        "tiktoken is required for document chunking.\n"
        "Install with: pip install tiktoken"
    )


@dataclass
class Chunk:
    """A chunk of a document."""
    text: str
    content_hash: str
    start_line: int
    end_line: int
    token_count: int
    chunk_index: int


def get_tokenizer(model: str = "text-embedding-3-small"):
    """Get tiktoken tokenizer for the embedding model."""
    # text-embedding-3-small uses cl100k_base encoding
    return tiktoken.get_encoding("cl100k_base")


def count_tokens(text: str, tokenizer=None) -> int:
    """Count tokens in text."""
    if tokenizer is None:
        tokenizer = get_tokenizer()
    return len(tokenizer.encode(text))


def hash_content(content: str) -> str:
    """Generate a hash for content to detect changes."""
    return hashlib.sha256(content.encode('utf-8')).hexdigest()[:16]


def chunk_document(
    content: str,
    max_tokens: int = 400,
    overlap_tokens: int = 80,
    min_chunk_tokens: int = 50
) -> List[Chunk]:
    """
    Split document into overlapping chunks.

    Args:
        content: The document text to chunk
        max_tokens: Maximum tokens per chunk (~400 is good for embeddings)
        overlap_tokens: Token overlap between consecutive chunks
        min_chunk_tokens: Minimum chunk size (avoid tiny trailing chunks)

    Returns:
        List of Chunk objects with text, line numbers, and metadata
    """
    tokenizer = get_tokenizer()

    # Split into lines with line numbers
    lines = content.split('\n')

    # If document is small enough, return as single chunk
    total_tokens = count_tokens(content, tokenizer)
    if total_tokens <= max_tokens:
        return [Chunk(
            text=content.strip(),
            content_hash=hash_content(content),
            start_line=1,
            end_line=len(lines),
            token_count=total_tokens,
            chunk_index=0
        )]

    chunks = []
    current_chunk_lines = []
    current_chunk_tokens = 0
    chunk_start_line = 1
    chunk_index = 0

    # Track token count per line for overlap calculation
    line_tokens = []
    for line in lines:
        line_tokens.append(count_tokens(line + '\n', tokenizer))

    i = 0
    while i < len(lines):
        line = lines[i]
        line_token_count = line_tokens[i]

        # Check if adding this line would exceed max_tokens
        if current_chunk_tokens + line_token_count > max_tokens and current_chunk_lines:
            # Create chunk from accumulated lines
            chunk_text = '\n'.join(current_chunk_lines)
            chunks.append(Chunk(
                text=chunk_text.strip(),
                content_hash=hash_content(chunk_text),
                start_line=chunk_start_line,
                end_line=chunk_start_line + len(current_chunk_lines) - 1,
                token_count=current_chunk_tokens,
                chunk_index=chunk_index
            ))
            chunk_index += 1

            # Calculate overlap: go back until we have ~overlap_tokens
            overlap_lines = []
            overlap_tokens_count = 0
            for j in range(len(current_chunk_lines) - 1, -1, -1):
                line_toks = line_tokens[chunk_start_line - 1 + j]
                if overlap_tokens_count + line_toks > overlap_tokens:
                    break
                overlap_lines.insert(0, current_chunk_lines[j])
                overlap_tokens_count += line_toks

            # Start new chunk with overlap
            current_chunk_lines = overlap_lines.copy()
            current_chunk_tokens = overlap_tokens_count
            chunk_start_line = chunk_start_line + len(current_chunk_lines) - len(overlap_lines) + (len(current_chunk_lines) - len(overlap_lines))

            # Recalculate start line properly
            chunk_start_line = i + 1 - len(overlap_lines)

        # Add line to current chunk
        current_chunk_lines.append(line)
        current_chunk_tokens += line_token_count
        i += 1

    # Don't forget the last chunk
    if current_chunk_lines:
        chunk_text = '\n'.join(current_chunk_lines)
        chunk_token_count = count_tokens(chunk_text, tokenizer)

        # If last chunk is too small and we have previous chunks, merge with previous
        if chunk_token_count < min_chunk_tokens and chunks:
            # Merge with previous chunk
            prev_chunk = chunks[-1]
            merged_text = prev_chunk.text + '\n' + chunk_text
            chunks[-1] = Chunk(
                text=merged_text.strip(),
                content_hash=hash_content(merged_text),
                start_line=prev_chunk.start_line,
                end_line=chunk_start_line + len(current_chunk_lines) - 1,
                token_count=count_tokens(merged_text, tokenizer),
                chunk_index=prev_chunk.chunk_index
            )
        else:
            chunks.append(Chunk(
                text=chunk_text.strip(),
                content_hash=hash_content(chunk_text),
                start_line=chunk_start_line,
                end_line=chunk_start_line + len(current_chunk_lines) - 1,
                token_count=chunk_token_count,
                chunk_index=chunk_index
            ))

    return chunks


def chunk_by_paragraphs(
    content: str,
    max_tokens: int = 400,
    overlap_paragraphs: int = 1
) -> List[Chunk]:
    """
    Alternative chunking strategy: split by paragraphs.

    Better for well-structured documents with clear paragraph breaks.

    Args:
        content: The document text
        max_tokens: Maximum tokens per chunk
        overlap_paragraphs: Number of paragraphs to overlap

    Returns:
        List of Chunk objects
    """
    tokenizer = get_tokenizer()

    # Split into paragraphs (blank line separated)
    paragraphs = content.split('\n\n')
    paragraphs = [p.strip() for p in paragraphs if p.strip()]

    if not paragraphs:
        return []

    # Track line numbers for each paragraph
    lines = content.split('\n')
    para_line_starts = []
    current_line = 1
    for para in paragraphs:
        para_line_starts.append(current_line)
        current_line += para.count('\n') + 2  # +2 for blank line separator

    chunks = []
    current_paras = []
    current_tokens = 0
    chunk_index = 0
    para_start_idx = 0

    for i, para in enumerate(paragraphs):
        para_tokens = count_tokens(para, tokenizer)

        if current_tokens + para_tokens > max_tokens and current_paras:
            # Create chunk
            chunk_text = '\n\n'.join(current_paras)
            start_line = para_line_starts[para_start_idx]
            end_line = para_line_starts[i - 1] + current_paras[-1].count('\n')

            chunks.append(Chunk(
                text=chunk_text,
                content_hash=hash_content(chunk_text),
                start_line=start_line,
                end_line=end_line,
                token_count=current_tokens,
                chunk_index=chunk_index
            ))
            chunk_index += 1

            # Start new chunk with overlap
            overlap_start = max(0, len(current_paras) - overlap_paragraphs)
            current_paras = current_paras[overlap_start:]
            current_tokens = sum(count_tokens(p, tokenizer) for p in current_paras)
            para_start_idx = i - len(current_paras)

        current_paras.append(para)
        current_tokens += para_tokens

    # Last chunk
    if current_paras:
        chunk_text = '\n\n'.join(current_paras)
        start_line = para_line_starts[para_start_idx] if para_start_idx < len(para_line_starts) else 1
        end_line = len(lines)

        chunks.append(Chunk(
            text=chunk_text,
            content_hash=hash_content(chunk_text),
            start_line=start_line,
            end_line=end_line,
            token_count=count_tokens(chunk_text, tokenizer),
            chunk_index=chunk_index
        ))

    return chunks


def should_chunk(content: str, threshold_tokens: int = 500) -> bool:
    """
    Determine if a document should be chunked.

    Small documents are more efficiently stored as single vectors.

    Args:
        content: Document text
        threshold_tokens: Documents above this size should be chunked

    Returns:
        True if document should be chunked
    """
    return count_tokens(content) > threshold_tokens
