# Error Handling Strategy
General Approach: A global exception handler will catch unexpected crashes, log the error to a file, and show a user-friendly message.

Logging: We will use Serilog to write application events and errors to a local log file for diagnostics.
