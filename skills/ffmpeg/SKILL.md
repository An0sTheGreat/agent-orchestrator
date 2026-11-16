---
name: ffmpeg
description: "Use when the user calls ffmpeg, /ffmpeg or $ffmpeg, or asks to cut, convert, compress, resize or inspect video or audio, make a GIF, turn renders or an image sequence into a video, or pull frames out of a video."
---

# /ffmpeg

## Purpose
Video, audio and image-sequence work from the command line with FFmpeg and ffprobe.

## Find it
`where ffmpeg` (a winget install puts it on PATH). If it is missing, tell the user (`winget install Gyan.FFmpeg`); do not download builds yourself.

## Always
- Inspect before and after: `ffprobe -v error -show_entries format=duration,size:stream=codec_type,codec_name,width,height,r_frame_rate -of json in.mp4`.
- Add `-hide_banner -loglevel error -y` so the output is only errors; `-y` overwrites, so write to a new file name, never over the input.
- Quote paths. Image sequences use printf patterns: `frame_%04d.png`.
- Writes go in the project; the sandbox may run ffmpeg fine (it needs no network). GPU encoders (`*_nvenc`, `*_qsv`, `*_amf`) may not open in the sandbox: fall back to `libx264`, or ask for `outside_sandbox`.
- Check the result: probe it, and look at a frame (`-frames:v 1 check.png`, then open the PNG) when the picture matters.

## Recipes
| Job | Command (after `ffmpeg -hide_banner -loglevel error -y`) |
|---|---|
| Cut 2 s from 0:01, re-encoded (frame-exact) | `-ss 1 -t 2 -i in.mp4 -c:v libx264 -crf 23 -c:a aac out.mp4` |
| Cut fast, no re-encode (snaps to keyframes) | `-ss 1 -t 2 -i in.mp4 -c copy out.mp4` |
| Compress for sharing | `-i in.mp4 -c:v libx264 -crf 28 -preset veryfast -vf "scale=-2:720" -c:a aac -b:a 96k out.mp4` |
| GPU encode (NVIDIA) | `-i in.mp4 -c:v h264_nvenc -preset p5 -cq 28 out.mp4` (`hevc_nvenc`, `av1_nvenc` also) |
| Good GIF | `-i in.mp4 -vf "fps=12,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse" out.gif` |
| Image sequence to video (renders) | `-framerate 30 -i render/frame_%04d.png -c:v libx264 -pix_fmt yuv420p -crf 18 out.mp4` |
| Video to frames | `-i in.mp4 -vf "fps=1,scale=640:-1" frames/f_%03d.png` (make the folder first) |
| Contact sheet (one image) | `-i in.mp4 -vf "fps=1/5,scale=320:-1,tile=4x3" -frames:v 1 sheet.png` |
| Scene cuts (times) | `-i in.mp4 -vf "select='gt(scene,0.3)',showinfo" -fps_mode vfr -f null -` then read `pts_time` in the log |
| Extract audio | `-i in.mp4 -vn -c:a libmp3lame -q:a 4 out.mp3` (or `-c:a copy out.m4a`) |
| Replace audio | `-i video.mp4 -i music.mp3 -map 0:v -map 1:a -c:v copy -shortest out.mp4` |
| Join same-format clips | list file `file 'a.mp4'` per line, then `-f concat -safe 0 -i list.txt -c copy out.mp4` |
| Speed up 2x | `-i in.mp4 -vf "setpts=0.5*PTS" -af "atempo=2" out.mp4` |
| Resize / pad to 1920x1080 | `-vf "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2"` |
| Burn subtitles | `-i in.mp4 -vf "subtitles=subs.srt" out.mp4` |
| Normalize loudness | `-i in.wav -af loudnorm=I=-16:TP=-1.5:LRA=11 out.wav` |
| Test clip (no input) | `-f lavfi -i testsrc2=size=1280x720:rate=30 -t 5 -pix_fmt yuv420p test.mp4` |

`-pix_fmt yuv420p` keeps H.264 playable everywhere (browsers, phones). `-crf`: lower is better quality and bigger (18 near-lossless, 23 default, 28 small).

## Frames for a vision model
To let a model "watch" a video, extract a few frames (`fps=1/N` or the scene cuts) or one contact sheet, and open those images; never send the video itself.

## Output
- The command(s), the probed result (duration, size, resolution, codecs) and a look at a frame when it matters.
