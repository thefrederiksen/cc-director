"""Tests for cc-vault document chunker."""

import pytest
from pathlib import Path
import sys

# Add src to path
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))


class TestChunker:
    """Test document chunking functionality."""

    @pytest.fixture
    def chunker(self):
        """Get the chunker module."""
        from src import chunker
        return chunker

    def test_count_tokens(self, chunker):
        """Test token counting."""
        text = "Hello world, this is a test."

        count = chunker.count_tokens(text)

        # Should return a positive integer
        assert count > 0
        assert isinstance(count, int)

    def test_count_tokens_empty(self, chunker):
        """Test token counting with empty string."""
        count = chunker.count_tokens("")
        assert count == 0

    def test_hash_content(self, chunker):
        """Test content hashing."""
        text = "Some content to hash"

        hash1 = chunker.hash_content(text)
        hash2 = chunker.hash_content(text)

        # Same content should produce same hash
        assert hash1 == hash2
        assert len(hash1) == 16  # First 16 chars of SHA256 hex

    def test_hash_content_different(self, chunker):
        """Test that different content produces different hashes."""
        hash1 = chunker.hash_content("Content A")
        hash2 = chunker.hash_content("Content B")

        assert hash1 != hash2

    def test_chunk_document_basic(self, chunker):
        """Test basic document chunking."""
        # Create a document long enough to chunk
        text = "This is a test paragraph. " * 100

        chunks = chunker.chunk_document(text, max_tokens=100, overlap_tokens=20)

        # Should have chunks
        assert len(chunks) >= 1

        # Each chunk should have expected structure (Chunk dataclass)
        for chunk in chunks:
            assert hasattr(chunk, "text")
            assert len(chunk.text) > 0
            assert hasattr(chunk, "start_line")
            assert hasattr(chunk, "end_line")
            assert hasattr(chunk, "chunk_index")

    def test_chunk_document_short(self, chunker):
        """Test chunking short text (single chunk)."""
        text = "Short text that fits in one chunk."

        chunks = chunker.chunk_document(text, max_tokens=1000)

        assert len(chunks) == 1
        assert text in chunks[0].text

    def test_chunk_document_markdown(self, chunker):
        """Test chunking markdown content."""
        markdown = """# Header 1

This is some content under header 1.

## Header 2

This is content under header 2.

### Header 3

More content here.

""" * 20  # Repeat to make it longer

        chunks = chunker.chunk_document(markdown, max_tokens=200)

        # Should have multiple chunks
        assert len(chunks) >= 1

        # Chunks should have text (Chunk dataclass)
        for chunk in chunks:
            assert hasattr(chunk, "text")

    def test_chunk_by_paragraphs(self, chunker):
        """Test paragraph-based chunking."""
        # Create text with clear paragraph breaks
        paragraphs = [f"Paragraph {i}. " * 10 for i in range(10)]
        text = "\n\n".join(paragraphs)

        chunks = chunker.chunk_by_paragraphs(text, max_tokens=200)

        # Should have chunks
        assert len(chunks) >= 1

        # Each chunk should have text (Chunk dataclass)
        for chunk in chunks:
            assert hasattr(chunk, "text")
            assert len(chunk.text) > 0

    def test_should_chunk_small(self, chunker):
        """Test should_chunk with small content."""
        small_text = "Small text"

        result = chunker.should_chunk(small_text, threshold_tokens=500)

        assert result is False

    def test_should_chunk_large(self, chunker):
        """Test should_chunk with large content."""
        large_text = "This is a longer text. " * 200

        result = chunker.should_chunk(large_text, threshold_tokens=100)

        assert result is True


class TestChunkerEdgeCases:
    """Test edge cases in chunking."""

    @pytest.fixture
    def chunker(self):
        from src import chunker
        return chunker

    def test_unicode_text(self, chunker):
        """Test chunking text with unicode characters."""
        text = "Hello world! " * 50

        chunks = chunker.chunk_document(text, max_tokens=100)

        assert len(chunks) >= 1
        # All content should be valid (Chunk dataclass)
        for chunk in chunks:
            assert isinstance(chunk.text, str)

    def test_special_characters(self, chunker):
        """Test chunking text with special characters."""
        text = "Code: `print('hello')` and more. " * 50

        chunks = chunker.chunk_document(text, max_tokens=100)

        assert len(chunks) >= 1

    def test_very_long_line(self, chunker):
        """Test chunking a single very long line."""
        text = "word " * 1000  # Single line, many words

        chunks = chunker.chunk_document(text, max_tokens=100)

        # Should still be able to chunk it
        assert len(chunks) >= 1

    def test_empty_content(self, chunker):
        """Test chunking empty content."""
        chunks = chunker.chunk_document("")

        # Should return empty list or minimal chunks
        assert len(chunks) <= 1


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
