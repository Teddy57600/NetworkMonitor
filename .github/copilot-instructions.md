# Copilot Instructions

## General Guidelines
- When a UI issue persists after a fix, re-investigate the exact styling/behavior before considering it resolved.
- When adding UI animations (including button press/enfoncement effects), prefer clearly visible, pronounced effects rather than very subtle transitions; ensure effects do not break native control behavior—verify real interaction and implement them without mutating the control's DOM during clicks.
- Reproduce visual references faithfully rather than interpreting them freely.
- For every new feature added to this project, always update README.md accordingly.

## Project-Specific Rules
- For the web interface, use a login page with the same design as the dashboard instead of HTTP Basic authentication.
- Reference a prebuilt image in Compose files instead of using `build:` as Docker Compose is not utilized in this project.