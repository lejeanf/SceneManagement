Scene Management

The main goal of this system is to be able to start the game from anywhere in the world and be able to load all the required visuals & scene logic you might have in this world.
All the static meshes are stored in subscenes leveraging the Entitty Component System to load the world as fast as possible. This part of the system uses "Volumes" & "relevant" entities do decide which volume-set to load (containing references to a bunch of scenes related to that set).
By checking the position of the relevant entities, we can then define which volumes are to be loaded. 

In parallel I also build a Additive-Loading system that loads content depending on the region or zone (using colliders to detect the player's position) to be able to use MonoBehavior scripts without having to recode the whole codebase. Each region or scenario can have dependency that will remain loaded until either a region or a scenario became irrelevant. The additive system loads scene additively depending on zone & region and upon scenario load/unload requests. 

Key elements:
- Loading scenes additively 
- Concept of Region & Zones
- Concept of Scenario
- Loading scene dynamically depending on position


------------------------------------------------------------------------------------------------------
Acknowledgment:
- Inspired by EntityComponentSystemSamples project
https://github.com/Unity-Technologies/EntityComponentSystemSamples
- Thanks to CodeMonkey for his advices.
[https://github.com/Unity-Technologies/EntityComponentSystemSamples](https://unitycodemonkey.com/)

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
Depedencies:
- Make sure to install UniTask as well: https://github.com/Cysharp/UniTask?tab=readme-ov-file#install-via-git-url

------------------------------------------------------------------------------------------------------
LICENCE:

<img src="https://licensebuttons.net/l/by-nc-sa/3.0/88x31.png"></img>
