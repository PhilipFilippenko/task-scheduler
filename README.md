# Task Scheduler

Windows tray app for planning the day as a timeline of tasks, modelled on an iPhone timeline app.

- Tasks as pills sized by duration, live progress of the running task
- Add/edit sheet with time and duration wheels, date chips and a calendar
- Own notification cards with a chime, floating always-on-top timer
- Glass look with a selectable accent colour, per-task icons
- Settings: start with Windows, accent colour, sound, pop-ups, timer corner and monitor, default task length

## Download

Grab `TaskScheduler.exe` from [Releases](https://github.com/PhilipFilippenko/task-scheduler/releases) and run it — a single file with .NET included, nothing to install (Windows 10/11, x64).
Put it in its own folder: tasks and settings are saved next to the exe. "Start with Windows" is in Settings.

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet publish -c Release -o app
app\TaskTimeline.exe --selftest
```

Run `app\TaskTimeline.exe` (`--tray` starts hidden in the tray, `--new` opens the add-task sheet).
Tasks and settings are stored next to the exe in `tasks.json` and `settings.json`.
