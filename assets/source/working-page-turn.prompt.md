# Page turn: built-in imagegen

Mode: built-in `image_gen.imagegen`, transparent background. Both sources are new edits of the existing G4-style reading model, not copied original-show animation.

## Page/hoof action source

Use case: identity-preserve. Asset: six sequential animation cels of a single page turn for the EXISTING G4 Rainbow Dash reading desktop pet.
Input image 1 is the EXACT EDIT TARGET resting-reading pose, 512x512 transparent square. Input image2 is its six-cel identity reference sheet. Preserve exact clean flat G4 TV animation style and the existing pony design. Keep every anatomical proportion, face, eyes, mane, tail, body, folded limbs, left forehoof supporting cheek, small white cloud, red book covers and book spine in EXACTLY the same coordinates in every cel. Do not animate the whole body, cloud, covers or head. No blinking in this action. The eyes continue gently looking down. Do NOT enlarge/zoom or change the pose. Use precisely the target's 512px framing per cel.
Output a transparent 1536x1024 sprite sheet in a strict 3-column by 2-row grid (six 512x512 cells), row-major timeline, no visible grid or text. Each cell is the same resting pony pose and open book. ONLY animate the screen-right cyan forehoof that currently rests at the maroon book right edge, and ONE cream-colored sheet of paper. The same paper leaf flips FROM SCREEN RIGHT TO SCREEN LEFT, hinged exactly at the book spine. The book is open, seen from its outside front covers. Covers MUST stay motionless and the same shape, no magic, no extra hooves.
Cel1: right forehoof lifts from the outside lower right edge of book towards the upper right paper corner, leaving the red cover revealed where the hoof was.
Cel2: right hoof touches the upper-right page corner; one cream leaf peels upward in a curled flap, anchored at the book spine.
Cel3: one large cream sheet stands almost VERTICAL above the book spine, its free top edge visibly 60–95 source pixels above the book top edge. It must read clearly as a paper PAGE, slightly bent with a thin pale-gold outline, not a second book or wing. Hoof guides the raised right corner.
Cel4: the same tall cream sheet passes OVER THE SPINE toward SCREEN LEFT, its free edge now left of center, gently curled, hoof following just behind.
Cel5: page settles onto the LEFT stack and flattens, small cream curl remaining; hoof releases and returns toward right cover.
Cel6: page lies flat on the left, right hoof has returned to EXACT resting position on the right cover like the reference. Quiet downcast reading face, identical to target.
The cream page needs a clear visible silhouette against blue coat and red cover so it is readable at 200px displayed height. Keep book cover corners and book spine registered precisely, NO book bending/closing/flipping. Preserve transparent background with no glowing halo or floor. Entire pony/cloud and all parts fit their original square framing. No labels, captions, shadows, panel borders, motion blur, 3D gradients, extra characters.

Saved source: `page-turn-sheet.png`.

## Hoof transition source

Use case: identity-preserve. Asset: six tiny forehoof-motion in-between poses for an existing desktop pony reading animation.
Input image1 is the EXACT ORIGINAL resting pose to preserve: G4 Rainbow Dash sitting on a small cloud, cheek supported on SCREEN-LEFT forehoof, open red hardcover book, screen-right forehoof touching the outside book right edge. Image2 is the existing page-turn animation and shows the final raised-hoof position. Draw SIX cels in a transparent1536x1024 3-column by2-row sprite sheet, each512x512 with precisely the same original image1 framing, anatomy, colors, head, eyes, mane/tail, body, cloud, legs and book corners. No labels/grid or backgrounds. Only the SCREEN-RIGHT FOREHOOF changes position; all pages remain FLAT AND STILL and the book remains motionless in these six cels. Do NOT lift the cheek-supporting left hoof. The eyes retain the exact soft downcast reading expression; no blink.
These are VERY SMALL consecutive upward positions of the right hoof, NOT six fully different poses. The right foreleg remains naturally attached to the same shoulder behind the book. Target page corner is the book's upper right edge. Original resting hoof center is near (370,354) in the512 square. Book spine top is near(311,311), right cover upper corner(382,280).
Cel1: hoof only a LITTLE raised from original resting position, center approximately(373,337), beside the book right edge; reveal book cover where it used to rest.
Cel2: hoof lifted about another20px, center(374,316), still along right book edge.
Cel3: hoof lifted to book top edge, center(374,293), naturally bent foreleg.
Cel4: hoof just above right page corner, center(365,271), closer to the paper.
Cel5: hoof curled over the right page corner, center(345,252), matching the raised foreleg silhouette in input2's FIRST cel, but pages are still flat.
Cel6: exact original input1 rest pose again, hoof center(370,354) on right outside book cover.
DO NOT use the old raised-high pose for all six; visibly distribute hoof positions UPWARD across cels1–5 as specified. Keep hoof volume the same. Only the arm/hoof and revealed book-cover patch change. No hand fingers, no extra limbs, no full body wobble, no new book text, no book tilting/closing, no paper lift here. Genuinely transparent background, clean flat G4 line art, no lighting gradients.

Saved source: `hoof-transition-sheet.png`. The generator did not follow the numerical hoof-center coordinates exactly; five useful raised-hoof poses were selected and reordered. They are not claimed as the exact requested coordinates. Return motion reuses these five poses in reverse and the final pose reuses the established reading frame005.

Assembly crops/registers only the new book and right-hoof region over the existing reading sprite. The face, body, mane/tail and cloud outside that region stay pixel-identical. Exact registration, selected source cels, repeats and durations are recorded in `page-assembly-record.json`.
