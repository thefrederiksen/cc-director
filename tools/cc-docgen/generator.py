"""Diagram generation using the diagrams library.

Generates C4 Level 1 (Context) and Level 2 (Container) diagrams.
"""

import logging
import os
import shutil
import sys
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

# Check for Graphviz before importing diagrams
GRAPHVIZ_NOT_INSTALLED = """Graphviz 'dot' command not found in PATH.

Graphviz may already be installed at: C:\\Program Files\\Graphviz\\bin

To fix:
1. Check if installed: dir "C:\\Program Files\\Graphviz\\bin\\dot.exe"
2. Add to PATH (PowerShell):
   $p = [Environment]::GetEnvironmentVariable("PATH", "User")
   [Environment]::SetEnvironmentVariable("PATH", "$p;C:\\Program Files\\Graphviz\\bin", "User")
3. Or install: winget install Graphviz.Graphviz
4. Restart terminal after PATH changes
"""


def _check_graphviz() -> None:
    """Check if Graphviz is installed and accessible.

    Raises:
        RuntimeError: If Graphviz is not found in PATH.
    """
    if shutil.which("dot") is None:
        raise RuntimeError(GRAPHVIZ_NOT_INSTALLED)


def generate_context_diagram(
    manifest: dict[str, Any],
    output_dir: Path,
    output_format: str = "png",
    verbose: bool = False,
) -> Path:
    """Generate C4 Level 1 System Context diagram.

    Args:
        manifest: Validated architecture manifest dictionary.
        output_dir: Directory to write output file.
        output_format: Output format (png or svg).
        verbose: Print verbose output.

    Returns:
        Path to generated diagram file.

    Raises:
        RuntimeError: If Graphviz is not installed.
    """
    _check_graphviz()

    # Import diagrams only after Graphviz check
    from diagrams import Diagram
    from diagrams.c4 import Person, System

    project = manifest["project"]
    system_info = manifest["context"]["system"]
    actors = manifest.get("context", {}).get("actors", [])

    diagram_name = f"{project['name']} - System Context"
    output_path = output_dir / "context"

    if verbose:
        logger.info("Generating context diagram: %s", diagram_name)

    # Change to output directory so diagrams library writes there
    original_dir = os.getcwd()
    os.chdir(output_dir)

    try:
        with Diagram(
            diagram_name,
            filename="context",
            show=False,
            direction="TB",
            outformat=output_format,
            graph_attr={
                "fontsize": "14",
                "bgcolor": "white",
                "pad": "0.5",
            },
        ):
            # Create the main system
            main_system = System(
                system_info["name"],
                description=system_info.get("description", ""),
            )

            # Create actors and their relationships
            persons = []
            external_systems = []

            for actor in actors:
                if actor["type"] == "person":
                    p = Person(
                        actor["name"],
                        description=actor.get("description", ""),
                    )
                    persons.append((p, actor))
                elif actor["type"] == "external_system":
                    ext = System(
                        actor["name"],
                        description=actor.get("description", ""),
                        external=True,
                    )
                    external_systems.append((ext, actor))

            # Draw relationships
            for p, actor in persons:
                rel = actor.get("relationship", {})
                p >> main_system

            for ext, actor in external_systems:
                rel = actor.get("relationship", {})
                # Direction depends on relationship description
                desc = rel.get("description", "").lower()
                if "spawned" in desc or "monitored" in desc or "uses" in desc.split():
                    main_system >> ext
                else:
                    ext >> main_system

    finally:
        os.chdir(original_dir)

    result_path = output_dir / f"context.{output_format}"
    if verbose:
        logger.info("Context diagram saved: %s", result_path)

    return result_path


def generate_container_diagram(
    manifest: dict[str, Any],
    output_dir: Path,
    output_format: str = "png",
    verbose: bool = False,
) -> Path:
    """Generate C4 Level 2 Container diagram.

    Args:
        manifest: Validated architecture manifest dictionary.
        output_dir: Directory to write output file.
        output_format: Output format (png or svg).
        verbose: Print verbose output.

    Returns:
        Path to generated diagram file.

    Raises:
        RuntimeError: If Graphviz is not installed.
    """
    _check_graphviz()

    # Import diagrams only after Graphviz check
    from diagrams import Cluster, Diagram
    from diagrams.c4 import Container, Person, System, SystemBoundary

    project = manifest["project"]
    system_info = manifest["context"]["system"]
    containers = manifest.get("containers", [])
    actors = manifest.get("context", {}).get("actors", [])

    diagram_name = f"{project['name']} - Container Diagram"
    output_path = output_dir / "container"

    if verbose:
        logger.info("Generating container diagram: %s", diagram_name)

    # Change to output directory so diagrams library writes there
    original_dir = os.getcwd()
    os.chdir(output_dir)

    try:
        with Diagram(
            diagram_name,
            filename="container",
            show=False,
            direction="TB",
            outformat=output_format,
            graph_attr={
                "fontsize": "14",
                "bgcolor": "white",
                "pad": "0.5",
            },
        ):
            # Create persons
            persons = []
            for actor in actors:
                if actor["type"] == "person":
                    p = Person(
                        actor["name"],
                        description=actor.get("description", ""),
                    )
                    persons.append(p)

            # Create external systems
            external_systems = []
            for actor in actors:
                if actor["type"] == "external_system":
                    ext = System(
                        actor["name"],
                        description=actor.get("description", ""),
                        external=True,
                    )
                    external_systems.append(ext)

            # Create system boundary with containers
            with SystemBoundary(system_info["name"]):
                container_nodes = []
                for cont in containers:
                    c = Container(
                        cont["name"],
                        technology=cont.get("technology", ""),
                        description=cont.get("description", ""),
                    )
                    container_nodes.append((c, cont))

            # Draw relationships
            # Persons connect to first container (usually UI)
            if persons and container_nodes:
                ui_container = container_nodes[0][0]
                for p in persons:
                    p >> ui_container

            # Connect containers based on project structure (UI -> Core -> Native)
            for i in range(len(container_nodes) - 1):
                curr_node = container_nodes[i][0]
                next_node = container_nodes[i + 1][0]
                curr_node >> next_node

            # Core/service containers connect to external systems
            if external_systems and len(container_nodes) > 1:
                core_container = container_nodes[1][0] if len(container_nodes) > 1 else container_nodes[0][0]
                for ext in external_systems:
                    core_container >> ext

    finally:
        os.chdir(original_dir)

    result_path = output_dir / f"container.{output_format}"
    if verbose:
        logger.info("Container diagram saved: %s", result_path)

    return result_path


def generate_all(
    manifest: dict[str, Any],
    output_dir: Path,
    output_format: str = "png",
    verbose: bool = False,
) -> list[Path]:
    """Generate all C4 diagrams from manifest.

    Args:
        manifest: Validated architecture manifest dictionary.
        output_dir: Directory to write output files.
        output_format: Output format (png or svg).
        verbose: Print verbose output.

    Returns:
        List of paths to generated diagram files.

    Raises:
        RuntimeError: If Graphviz is not installed.
    """
    output_dir.mkdir(parents=True, exist_ok=True)

    results = []
    results.append(generate_context_diagram(manifest, output_dir, output_format, verbose))
    results.append(generate_container_diagram(manifest, output_dir, output_format, verbose))

    return results
