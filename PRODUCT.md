# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

Players use Hexor Control Center on a Windows desktop to review and persist supported AION automation settings. The product is used alongside the game and original vendor updater; it observes the vendor runtime bridge and issues only explicit, operator-initiated session controls. It does not remotely control the game, never starts automation, and never manages game accounts.

## Product Purpose

Hexor makes verified configuration settings easier to understand and edit while preserving customer configuration files and explicit user control over every action.

## Positioning

Hexor is a safe settings control center: it presents only verified storage-backed options, makes limitations explicit, and opens the original updater only when a customer asks.

## Operating Context

The interface is a Windows WPF application with seven settings areas, diagnostics, signed GitHub self-update support, and an on-demand vendor updater handoff.

## Capabilities and Constraints

- Preserve existing bindings, configuration behavior, updater path resolution, completion verification, and installer layout.
- Never automatically launch vendor software or Legion Commander.
- The original updater owns authentication and customer key entry.
- The app must remain keyboard accessible, DPI aware, and usable at common desktop scales.

## Brand Commitments

The product uses the Hexor name and supplied transparent Hexor icon/logo assets. The redesign brief binds it to a premium, dark purple gaming-control-panel character without copying competitor products.

## Evidence on Hand

Existing WPF implementation and tests are in this repository. Brand assets are in `Assets/`. No customer testimonials, performance claims, or game-control claims may be fabricated.

## Product Principles

- Make verified settings easy to scan and hard to misinterpret.
- Keep customer actions deliberate and reversible where possible.
- Preserve native Windows affordances and accessibility.
- Let Hexor branding add precision rather than noise.

## Accessibility & Inclusion

Keyboard focus, readable contrast, native automation names, and Windows scaling are required.
