# vigilant-eureka
Winc Clicker

The C# rewrite is done. It compiles cleanly with the Mono C# compiler here, but I haven't run it on Windows, so try the Test click first.

## To build (30 seconds, nothing to install):

- Put ``ScheduledClicker.cs`` and ``build.bat`` in the same folder.
- Double-click ``build.bat``. It uses the C# compiler already built into Windows 11, so you don't need Visual Studio.
- Run the ``ScheduledClicker.exe it produces.

#### I wrote the code in C# 5 syntax so that Windows' built-in compiler can build it. ``ScheduledClicker.csproj`` is optional and only for building with Visual Studio or ``dotnet build``.

#### Size: expect the exe to be roughly 30-60 KB instead of 10 MB. It needs no extra runtime, because .NET Framework 4.8 is already part of Windows 11.

## Features carried over

- Jobs: start time, X/Y, click count, gap, mouse button and repeat-daily, as before.
- Editing: double-click any cell in the job list to edit it in place. Button is a dropdown and Daily is a checkbox.
- Scroll to change time: scrolling over the start time changes hours, minutes or seconds depending on where the pointer is. The Up/Down arrow keys work too.
- Pick position and Test click: both use a 3-second countdown, and both are visible on launch. The window is sized to fit all its controls at any Windows display scaling.
- Import from JSON: it reads the same format as before, and your existing ``example_jobs.json`` and ``clicker_jobs.json`` files work unchanged.
- Saved jobs: they save to ``clicker_jobs.json`` next to the exe.
- Stop: F8 stops the scheduler from anywhere, or use the Stop button.

## Two small improvements

- Gap timing: the gap now runs from one click start to the next, so timing stays accurate even with very short gaps.
- Screen scaling: the app is set up so X/Y are real screen pixels at 125% or 150% scaling.
