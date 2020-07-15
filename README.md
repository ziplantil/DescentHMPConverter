# DescentHMPConverter
This is a tool that allows converting between standard MIDI (1.0) files and the .HMP/.HMQ files used in the games ''Descent'' and ''Descent II''.
The latter format is more accurately known as HMI MIDI Type-P, a format designed by Human Machine Interfaces Inc. and used in their SOS 
(''Sound Operating System''), a sound engine found in many 90s DOS games.

The program is internally fairly small due to it using [LibDescent](https://github.com/InsanityBringer/LibDescent), but there are still some
details to take into account to make .HMP tracks play properly in Descent. This tool takes care of that.

It also has the capability to convert .MIDIs automatically into FM-compatible .HMQs too. (This isn't currently implemented, but it might be one day.)

