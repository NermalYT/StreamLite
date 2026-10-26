# Speaks test lines with Windows' OneCore voices (David, Mark, Zira) into WAV files (WinRT, no system changes).
param([string]$Out)
New-Item -ItemType Directory -Force $Out | Out-Null
$null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType = WindowsRuntime]
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
$lines = @(
  "Hey, can you hear me alright from over here?",
  "Push left, there's one behind the boxes on the stairs.",
  "Okay I'm going to grab a drink, give me one second.",
  "Nice shot, that was actually insane, how did you see him?",
  "I think we should rotate to B, they're stacking A again.",
  "Did you see the new update? They changed the whole map.",
  "Honestly I'm so tired, one more game and then I'm going to bed.",
  "Can you grab some milk on the way back from the store?",
  "No way, that's so funny, wait let me show you something.",
  "I'm going to order food, do you want anything from there?",
  "The weather is supposed to be nice this weekend, let's go out.",
  "Wait, wait, wait, he's still alive, he's in the corner."
)
$synth = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
function Save($stream, $path) {
  $reader = New-Object Windows.Storage.Streams.DataReader($stream.GetInputStreamAt(0))
  $size = [uint32]$stream.Size
  $null = Await ($reader.LoadAsync($size)) ([uint32])
  $bytes = New-Object byte[] $size
  $reader.ReadBytes($bytes)
  [IO.File]::WriteAllBytes($path, $bytes)
}
# Each voice as it is, and higher ("<name>High": a high-pitched man, +35%; a higher woman, +25%), spoken with SSML.
foreach ($v in [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices) {
  $name = ($v.DisplayName -split ' ')[1]
  $synth.Voice = $v
  $up = if ($v.Gender -eq 'Female') { '+25%' } else { '+35%' }
  $i = 0
  foreach ($l in $lines) {
    Save (Await ($synth.SynthesizeTextToStreamAsync($l)) ([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])) (Join-Path $Out ("{0}-{1:00}.wav" -f $name, $i))
    $ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><prosody pitch='$up'>" + [Security.SecurityElement]::Escape($l) + "</prosody></speak>"
    Save (Await ($synth.SynthesizeSsmlToStreamAsync($ssml)) ([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])) (Join-Path $Out ("{0}High-{1:00}.wav" -f $name, $i))
    $i++
  }
  "$name : $i lines (and $name" + "High, $up)"
}
