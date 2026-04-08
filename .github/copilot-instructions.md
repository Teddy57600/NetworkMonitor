# Copilot Instructions

## General Guidelines
- When a UI issue persists after a fix, re-investigate the exact styling/behavior before considering it resolved.
- When adding UI animations, prefer clearly visible effects rather than very subtle transitions.

## Project-Specific Rules
- For the web interface, use a login page with the same design as the dashboard instead of HTTP Basic authentication.
- Reference a prebuilt image in Compose files instead of using `build:` as Docker Compose is not utilized in this project.