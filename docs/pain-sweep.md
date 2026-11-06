# Pain sweep: time to break over intensity and suppression

`tools/PainSweep/Program.cs` runs the pain box puppet over a grid of box
intensity (rows) and withdrawal-reflex suppression strength (columns,
`PainBoxParameters.MaxSuppression`: the largest fraction of the reflex the will
can hold down). It is engine-free and deterministic; the same build prints the
same table.

## Reading the table

Each cell is the median time-to-break in seconds over 9 consecutive seeds (1..9;
seeds vary nociceptor gain, resolve drain and stimulus jitter). Everything else
is at its default, including objective pull 0.5 and a 120 s limit.

- `never`: no seed's will broke within 120 s. This is a result, not an error.
- `95.4(8/9)`: only 8 of 9 seeds broke; the median is over those 8, so it
  understates how long the ninth lasted.
- Higher suppression breaks the will sooner here. Holding the reflex down costs
  strain, strain drains resolve, and at suppression 0 there is no strain, so
  pain alone drains resolve too slowly to break it. That is how this model is
  built, not a finding about people.

## What it does not capture

- Every default is a placeholder. None of the numbers is measured physiology, so
  read the shape of the table, not the seconds.
- One hand, one stimulus channel, no body, no physics, no balance, no adaptation
  or habituation beyond the nerve's sensitisation, no fear, expectation or
  distraction.
- "Will" is one scalar draining toward zero. Real people trade off, bargain, and
  break in ways that are not a threshold crossing.
- Suppression is a fixed ceiling scaled by remaining resolve; nothing trains,
  tires or recovers it except through resolve.
- Nine seeds is a smoke grid, not a distribution. The table shows medians only.

## Question for the designer

Is time-to-break the right measure? It collapses a whole run to one number and
treats "never" as an outcome of equal weight to "late". Candidates: time to
first flinch, flinch count, area under the pain curve before the hand leaves,
peak strain, or the fraction of seeds that break by a deadline. Which one says
what a player should feel?

## Build and run (no `dotnet restore`)

```
D=/opt/commons/opt/dotnet; REF=$D/packs/Microsoft.NETCore.App.Ref/10.0.14/ref/net10.0
dotnet $D/sdk/10.0.403/Roslyn/bincore/csc.dll -nologo -nullable:enable -target:exe \
  -out:$TMPDIR/painsweep/PainSweep.dll -noconfig -nostdlib+ $(for f in $REF/*.dll; do echo -n "-r:$f "; done) \
  src/BadDay.Core/*.cs tools/PainSweep/Program.cs
printf '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.14"}}}' \
  > $TMPDIR/painsweep/PainSweep.runtimeconfig.json
dotnet $TMPDIR/painsweep/PainSweep.dll
```
