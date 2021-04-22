# Voxelis
Dedicated repo for Voxelis

```
FS16 performance
DO nothing after startup, mc world load scene, render radius 320 / 360
shadow draw vertex but default shader ( = basically no shadows)
Release in Editor Play mode

Plain (w/o LOD code)	~95 FPS
LOD 0 (16^3)		~88 FPS
LOD 1 (8^3)		~130 FPS
LOD 2 (4^3)		~165 FPS
LOD 3 (2^3)		~176 FPS
LOD 4 (1^3)		~175 FPS	(LOL too ugly and no benefits)
All LODs (64 base)	~145 FPS
All LODs (24 base)	~160 FPS
```
