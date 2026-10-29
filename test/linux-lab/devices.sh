#!/usr/bin/env bash
# Test devices like a real desk: headphones (a sink that goes nowhere), speakers (another), and a microphone whose
# sound is whatever is played into "Test mic feed" (paplay --device=lab_mic_feed voice.wav).
set -u
pactl load-module module-null-sink sink_name=lab_headphones sink_properties=device.description="Sennheiser_MOMENTUM_5_(lab)" >/dev/null
pactl load-module module-null-sink sink_name=lab_speakers sink_properties=device.description="Realtek_Speakers_(lab)" >/dev/null
pactl load-module module-null-sink sink_name=lab_mic_feed sink_properties=device.description="Test_mic_feed" >/dev/null
pactl load-module module-remap-source master=lab_mic_feed.monitor source_name=lab_mic source_properties=device.description="Blue_Yeti_(lab)" >/dev/null
pactl set-default-sink lab_headphones
pactl set-default-source lab_mic
pactl list short sinks; pactl list short sources
