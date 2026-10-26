# Arts & Crafts: Weaver & Tailor

Expanded weaving & clothmaking mechanics for Vintage Story.

## Introduction


## Implemented Features

### Fiber processing from Immersive Fibercraft

- Flying Shuttle Loom model & crafting mechanics (12 twine > 4 linen)
- Spinning Wheel model & crafting mechanics (2 fiber > 1 twine)
- Drop Spindle model & crafting mechanics (4 fiber > 1 twine); Twine grid recipe disabled by default

### Miscellaneous Features

- Can dye twine
- Golden twine (no crafting recipe)

#### Weaver
**Make fabric with custom patterns**

- Draft based weaving mechanics for the Flying Shuttle Loom
- Weaved linen can be placed on ground and walls like vanilla linen
- Weaved cloth can be placed on ground and walls using a hammer in the off hand slot
- Weaving fully non-dyed twine yields vanilla linen
- Linen weaved from single color twine can be crafted into vanila cloth  
- Tapestry loom & tapestry crafting mechanics
- Embroidery crafting mechanics
- Make carpets directly on the loom

#### Tailor
**Customize garments**

- Sewing crafting mechanics on a mannequin
- Accepts weaved cloth or linen indifferently
- Accepts other textiles: leather, pelts
- Some parts such as buttons accept metal chunks or boards instead
- Embroidery mechanic directly on the cloth while it is being sewn
- Extended system to add new garments for modders, recipes & models

Garments models:
- Hats: ..., ...
...

## Roadmap

- Immersive Fibercraft: Ability to convert a mod from Immersive fibercraft / Convert back to Immersive Fibercraft
- Compatibility with mods introducing new textiles
- Make flags
- Clothier trait & Tailor class update

## Known issues

**Bugs to be fixed or poor implementation**

## Known Gaps

**Broader open reflection on the mod**

### Weaving draft complexity

Current loom uses weaving drafts to create the cloth's patterns. The system is quite hard to understand at first (I know I struggled when I discovered it). But it is much more immersive than simple "pixel painting" each row (which is too close to tapestry) and allows for easy-ish mass production. I'd love feedback from both weavers and complete amateurs...

How to properly explain the system?

Could we preserve the current system but make it more immersive while not being tedious? Changing the weft color or moving the shafts manually is a pain. 

### Early technologies

Weaving and clothmaking are ancient arts. But this mod's first real usable implementation is the Flying Shuttle Loom, invented in the 18th century. Current solution is to make the Flying Shuttle Loom a rather early game item easy to craft. 

Hand looms are an attempt to bridge this gap. The issue is not to make them but to design satisfying crafting mechanics. But how to make them interesting gameplay wise? An option people would go to if they can' build a flying shuttle loom yet, but inferior to it?

### Making clothes with Weaved fabrics

In the current version, weaved patterns are applied to an unwrap of the cloth's mesh, with different parts where one can apply different weaved cloth. This result is some unelegant problems:
- patterns may join unelegantly on model joints
- patterns may not scale well on different sizes of the cloth
- the cost of making a garment can scale weirdly, for example be high due to a large amount of customizable parts for a rather small garment.
- some garments are not possible, such as the Tail Hats
- Non square model faces can make patterns behave horribly

It is sometimes better to directly embroider whatever the players want directly on the cloth. "Don't bother, embroider everything" is NOT what I want for this mod.


### The cost of making clothes

How can we scale the cost of making

# Credits

This mod is largely built on top of the work of HeadPilgrim in [Immersive Fibercraft](https://github.com/HeadPilgrim/vs-spinningwheel) and [In mod db](https://mods.vintagestory.at/show/mod/34327). It wouldn't have been possible without their work. Credits extend to all contributors to that mod: 
- ZapSplat For the loom foley sound effects
- Captanredbeard — Advice and guidance during initial development
- Ruddi (Medieval Fashion) — Spinning wheel retexture
- DeanBro — Ukrainian translation
- Zsuatem — Polish Translation
- PiatraAlatyr and Egil_Raud — Russian Translation
- k1llo — Belarusian Translation
- Smjergaes — Icelandic Translation


## Compatibility

- Immersive Fibercraft: *Full incompatibility* - While not being a fork, the present mod is made from Immersive fibercraft and should not be run with it. They will not break if ran together but introduce duplicated content


## How AI is used in this project

Generative AI in the form of coding agents has been used extensively to help the development process, writing code and solving bugs

I do not plan to use generative AI in any form to generate any illustration, artwork, sound assets.

Every communication destined to people, including this README, the mod db page, any post promoting my work are 100% human written.
