Scene Management

Description:
- Loading scenes additively 
- Concept of Region & Zones
- Concept of Scenario
- Loading scene dynamically depending on position

Both system are living side-by-side.
The dynamic systems handles the loading of all static content (environment) using subscenes.
The additive system loads scene additively depending on zone & region and upon scenario load/unload requests. Each region or scenario can have dependency that will remain loaded until either a region or a scenario became irrelevant.

------------------------------------------------------------------------------------------------------
Compatibility:
- URP
- HDRP
- Unity 6000.0.24f1 and above.


------------------------------------------------------------------------------------------------------
How to install the package:
- add new scopedRegisteries in ProjectSettings/Package manager
- name: jeanf
- url: https://registry.npmjs.com
- scope fr.jeanf

------------------------------------------------------------------------------------------------------
How to install:
- Install UniTask: https://github.com/Cysharp/UniTask?tab=readme-ov-file#install-via-git-url

------------------------------------------------------------------------------------------------------
LICENCE:

<img src="https://licensebuttons.net/l/by-nc-sa/3.0/88x31.png"></img>
