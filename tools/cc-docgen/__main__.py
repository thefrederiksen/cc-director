"""Entry point for running cc-docgen as a module.

Usage: python -m cc-docgen generate
"""

from cc_docgen.cli import cli

if __name__ == "__main__":
    cli()
