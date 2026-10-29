#!/usr/bin/env bash
# Test devices like a real desk, named the way PipeWire names USB devices (so StreamLite treats them as hardware):
# headphones, speakers (both go nowhere - no sound ever reaches a real speaker), a second mic, and a microphone whose
# sound is whatever is played into "lab_mic_feed" (paplay --device=lab_mic_feed voice.wav).
set -u
sink() { pactl load-module module-null-sink sink_name="$1" "sink_properties=device.description=\"$2\" device.bus=usb device.form_factor=$3" >/dev/null; }
sink alsa_output.usb-Sennheiser_MOMENTUM_5-00.analog-stereo "Sennheiser MOMENTUM 5" headphone
sink alsa_output.pci-0000_00_1f.3.analog-stereo "Built-in Audio Analog Stereo" speaker
pactl load-module module-null-sink sink_name=lab_mic_feed "sink_properties=device.description=\"Lab mic feed\"" >/dev/null
pactl load-module module-null-sink sink_name=lab_mic2_feed "sink_properties=device.description=\"Lab mic 2 feed\"" >/dev/null
pactl load-module module-remap-source master=lab_mic_feed.monitor source_name=alsa_input.usb-Blue_Microphones_Yeti_Stereo_Microphone-00.analog-stereo "source_properties=device.description=\"Yeti Stereo Microphone\" device.bus=usb" >/dev/null
pactl load-module module-remap-source master=lab_mic2_feed.monitor source_name=alsa_input.usb-HyperX_QuadCast_S-00.analog-stereo "source_properties=device.description=\"HyperX QuadCast S\" device.bus=usb" >/dev/null
pactl set-default-sink alsa_output.usb-Sennheiser_MOMENTUM_5-00.analog-stereo
pactl set-default-source alsa_input.usb-Blue_Microphones_Yeti_Stereo_Microphone-00.analog-stereo
pactl list short sinks; pactl list short sources
