# Task Scheduler

Windows tray app for planning the day as a timeline of tasks.

- Tasks as pills sized by duration, live progress of the running task
- Add/edit sheet with time and duration wheels, date chips, a calendar and an icon picker
- Routines: save a sequence of tasks and lay it onto any day in one click (also from the tray)
- Own notification cards with a chime, floating always-on-top timer
- Glass look with a selectable accent colour, per-task icons
- Settings: start with Windows, accent colour, sound, pop-ups, timer corner and monitor, default task length

## Download

Grab `TaskScheduler.exe` from [Releases](https://github.com/PhilipFilippenko/task-scheduler/releases) and run it — a single file with .NET included, nothing to install (Windows 10/11, x64).
Put it in its own folder: tasks, routines and settings are saved next to the exe. "Start with Windows" is in Settings.

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet publish -c Release -o app
app\TaskTimeline.exe --selftest
```

Run `app\TaskTimeline.exe` (`--tray` starts hidden in the tray, `--new` opens the add-task sheet).
Data is stored next to the exe in `tasks.json`, `routines.json` and `settings.json`.

## Credits

The idea and the visual timeline concept come from [Structured](https://structured.app), a day planner for iPhone and Android.
This project is an independent, open-source take on that idea for the Windows desktop. It was written from scratch: no code, artwork or other assets from Structured were used.
It is not affiliated with, endorsed by or connected to Structured or its developers. "Structured" is the name of their app and belongs to them.

## License

[MIT](LICENSE)
