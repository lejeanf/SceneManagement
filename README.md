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
How to use:
1: setup some volumes to load subscenes dynamically
<img width="357" alt="location_volume_auhoring" src="https://github.com/user-attachments/assets/7d83da53-d16d-41e3-a7ba-a7408950fe2b" />
<img width="830" alt="location_volumes" src="https://github.com/user-attachments/assets/4050f2de-f5c2-4e63-83a6-d20a1dbdcf1c" />
2: setup some volume sets for your subscenes
<img width="355" alt="volume_sets" src="https://github.com/user-attachments/assets/a087659f-a5a6-4e6a-b8ce-662f25f76a42" />
3: add a relevant authoring script to some gameObject (it will follow the MainCamera).
<img width="355" alt="relevantAuthoring" src="https://github.com/user-attachments/assets/70f5ade5-9c65-4c88-8d2a-fbeb3eb25fd9" />
4: Create and place zones & zoneContainers to detect the player's current position (in the future this step will be removed to as volumes should be enough to retreive this information).
<img width="358" alt="ZoneContainer" src="https://github.com/user-attachments/assets/5b25e7f9-80e9-4330-9140-a5e2eb7f0cb0" />
<img width="279" alt="scriptable_objects" src="https://github.com/user-attachments/assets/ea1ab562-0256-4a46-a28e-ae81031b3d7d" />
5: Import the AdditiveLoading prefab in your scene from the samples
<img width="356" alt="system-prefab" src="https://github.com/user-attachments/assets/4b9e37ff-9721-4012-aa41-4b1df236c53c" />
<img width="157" alt="Samples" src="https://github.com/user-attachments/assets/fc147a92-3359-4db4-a5f1-40ea3ef92204" />
6: setup your & organize scriptable objects:
<img width="359" alt="so_scenarios" src="https://github.com/user-attachments/assets/7c8caa9a-47da-4b67-8f1b-0d77d53a78ac" />
<img width="355" alt="so_zone" src="https://github.com/user-attachments/assets/6406484c-3751-4543-ad48-b146414efd5b" />
<img width="356" alt="so_region" src="https://github.com/user-attachments/assets/ebf4b91d-7762-4c20-9485-8dbf86d5ad65" />
<img width="234" alt="region_data" src="https://github.com/user-attachments/assets/8386ff82-f2a2-4dbb-b279-3c674c00d1d3" />
7: setup your spawn points per region, note that you can create a different initial spawn point, only one region should have the initial spawn point (later will be overriten by last player position when last playing the game)
<img width="806" alt="sapwn_points" src="https://github.com/user-attachments/assets/968fd020-2c58-4873-9f69-2b6f9943d56c" />



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
