@echo off
REM ---------------------------------------------------------------------------
REM  Double-click this to build the report and open it.
REM
REM  %~dp0 is the folder this file is sitting in, so the script works wherever
REM  the repository is cloned - no absolute paths to go stale.
REM ---------------------------------------------------------------------------

title PursuitHQ metrics

cd /d "%~dp0PursuitHQ.Metrics"

dotnet run

REM Only holds the window open when something went wrong. On success the report
REM is already open in the browser and there is nothing here worth reading.
if errorlevel 1 (
    echo.
    echo ---------------------------------------------------------------
    echo Something went wrong. The message above says what.
    echo.
    echo If it is asking for PURSUITHQ_METRICS_DB, see README.md - the
    echo connection string is set once with setx and needs a NEW terminal
    echo afterwards.
    echo ---------------------------------------------------------------
    echo.
    pause
)
