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
Licence:

<a rel="license" href="http://creativecommons.org/licenses/by-nc-sa/4.0/"><img alt="Licence Creative Commons" style="border-width:0" src="https://i.creativecommons.org/l/by-nc-sa/4.0/88x31.png" /></a><br />Ce(tte) œuvre est mise à disposition selon les termes de la <a rel="license" href="http://creativecommons.org/licenses/by-nc-sa/4.0/">Licence Creative Commons Attribution - Pas d’Utilisation Commerciale - Partage dans les Mêmes Conditions 4.0 International</a>.

This license lets others remix, adapt, and build upon your work non-commercially, as long as they credit you and license their new creations under the identical terms.


