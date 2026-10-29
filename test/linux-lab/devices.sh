#!/usr/bin/env bash
# Test devices like a real desk, named the way PipeWire names USB devices (so StreamLite treats them as hardware):
# headphones, speakers (both go nowhere - no sound ever reaches a real speaker), a second mic, and a microphone whose
# sound is whatever is played into "lab_mic_feed" (paplay --device=lab_mic_feed voice.wav).
# Properties go in double quotes with the name in single quotes - the only form both PulseAudio and PipeWire read whole.
set -u
props() { printf '"device.description='"'"'%s'"'"' %s"' "$1" "$2"; }
sink() { pactl load-module module-null-sink sink_name="$1" "sink_properties=$(props "$2" "$3")" >/dev/null; }
mic() { pactl load-module module-remap-source master="$1.monitor" source_name="$2" "source_properties=$(props "$3" "device.bus=usb device.form_factor=microphone")" >/dev/null; }
sink alsa_output.usb-Sennheiser_MOMENTUM_5-00.analog-stereo "Sennheiser MOMENTUM 5" "device.bus=usb device.form_factor=headphone"
sink alsa_output.pci-0000_00_1f.3.analog-stereo "Built-in Audio Analog Stereo" "device.bus=pci device.form_factor=speaker"
sink lab_mic_feed "Lab mic feed" ""
sink lab_mic2_feed "Lab mic 2 feed" ""
mic lab_mic_feed alsa_input.usb-Blue_Microphones_Yeti_Stereo_Microphone-00.analog-stereo "Yeti Stereo Microphone"
mic lab_mic2_feed alsa_input.usb-HyperX_QuadCast_S-00.analog-stereo "HyperX QuadCast S"
pactl set-default-sink alsa_output.usb-Sennheiser_MOMENTUM_5-00.analog-stereo
pactl set-default-source alsa_input.usb-Blue_Microphones_Yeti_Stereo_Microphone-00.analog-stereo
pactl list short sinks; pactl list short sources
