from setuptools import setup, find_packages

setup(
    name="cc_director",
    version="0.1.0",
    description="Cross-platform job scheduler with cron-style scheduling",
    author="Soren Frederiksen",
    packages=find_packages(),
    python_requires=">=3.10",
    install_requires=[
        "croniter>=2.0.0",
        "click>=8.0.0",
        "fastapi>=0.109.0",
        "uvicorn[standard]>=0.27.0",
        "jinja2>=3.1.0",
        "python-multipart>=0.0.6",
        "websockets>=12.0",
    ],
    extras_require={
        "windows": ["pywin32>=306"],
        "dev": ["pytest>=7.0.0", "pytest-cov>=4.0.0"],
    },
    entry_points={
        "console_scripts": [
            "cc_director_service=cc_director.service:main",
            "cc_scheduler=cc_director.scheduler:main",
        ],
    },
)
