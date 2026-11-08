These files are the C# reference's output, not this port's. Regenerate from the repo root
(see the README's build note for the `csc` route when `dotnet restore` is unavailable):

    dotnet run --project src/BadDay.Cli -- run <the args on the first line> > file.txt

The first line (`# baddaysim run ...`) records the arguments; the rest is `Render()` plus `Summary()`.
