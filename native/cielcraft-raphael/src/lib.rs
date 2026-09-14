//! C ABI wrapper around raphael-solver (spec §13/§14).
//!
//! The custom-recipe path is used so the plugin supplies live recipe values
//! (rlvl, max progress/quality/durability) read from the game, and no recipe
//! database is consulted here.

use raphael_data::{get_game_settings, CrafterStats, CustomRecipeOverrides};
use raphael_sim::{Combo, Effects, SimulationState};
use raphael_solver::{AtomicFlag, MacroSolver, SolverSettings};

#[repr(C)]
pub struct RaphaelInput {
    pub recipe_level: u16,
    pub max_progress: u16,
    pub max_quality: u16,
    pub max_durability: u16,
    pub craftsmanship: u16,
    pub control: u16,
    pub cp: u16,
    pub target_quality: u16,
    pub initial_quality: u16,
    pub level: u8,
    pub is_expert: u8,
    pub manipulation: u8,
    pub heart_and_soul: u8,
    pub quick_innovation: u8,
    pub adversarial: u8,
    pub backload_progress: u8,
    /// Mid-craft solve: exclude first-step-only actions (Muscle Memory, Reflect, Trained Eye).
    pub exclude_first_step_actions: u8,
    /// Waste Not active: exclude Prudent Synthesis/Touch (unusable under it).
    pub exclude_prudent: u8,
}

/// Live state of an in-progress craft for `raphael_solve_from_state`.
/// Buff fields are remaining steps (Inner Quiet: stacks), 0 = inactive.
#[repr(C)]
pub struct RaphaelLiveState {
    pub progress: u16,
    pub quality: u16,
    pub durability: u16,
    pub cp: u16,
    pub inner_quiet: u8,
    pub waste_not: u8,
    pub innovation: u8,
    pub veneration: u8,
    pub great_strides: u8,
    pub muscle_memory: u8,
    pub manipulation: u8,
    pub trained_perfection_available: u8,
    pub heart_and_soul_available: u8,
    pub quick_innovation_available: u8,
    pub trained_perfection_active: u8,
    pub heart_and_soul_active: u8,
    /// 0 = none, 1 = synthesis begin (step 1), 2 = after Basic Touch, 3 = after Standard Touch.
    pub combo: u8,
}

pub const ERR_INVALID_ARGS: i32 = -1;
pub const ERR_SOLVER_FAILED: i32 = -2;
pub const ERR_PANIC: i32 = -3;
pub const ERR_BUFFER_TOO_SMALL: i32 = -4;
pub const ERR_STATE_IS_FINAL: i32 = -5;

/// Solves the craft described by `input`.
///
/// On success, writes up to `out_capacity` FFXIV action ids (as returned by
/// Raphael's `Action::action_id`, i.e. CRP-flavored for per-job craft actions)
/// into `out_actions` and returns the number of actions. Negative = error.
///
/// # Safety
/// `input` must point to a valid `RaphaelInput`, and `out_actions` to a
/// writable buffer of at least `out_capacity` u32 values.
#[no_mangle]
pub unsafe extern "C" fn raphael_solve(
    input: *const RaphaelInput,
    out_actions: *mut u32,
    out_capacity: i32,
) -> i32 {
    if input.is_null() || out_actions.is_null() || out_capacity <= 0 {
        return ERR_INVALID_ARGS;
    }

    let input = &*input;
    let out = std::slice::from_raw_parts_mut(out_actions, out_capacity as usize);

    write_result(std::panic::catch_unwind(move || solve(input).ok_or(ERR_SOLVER_FAILED)), out)
}

/// Solves the remainder of an in-progress craft from its live state (spec §17):
/// same recipe/stats as `raphael_solve`, but the search starts at `live`
/// (progress, quality, durability, CP and active effects) instead of the
/// synthesis-begin state. `input.target_quality` is the absolute quality goal;
/// `input.initial_quality` is ignored (the live quality already includes it).
///
/// # Safety
/// `input` and `live` must point to valid structs, and `out_actions` to a
/// writable buffer of at least `out_capacity` u32 values.
#[no_mangle]
pub unsafe extern "C" fn raphael_solve_from_state(
    input: *const RaphaelInput,
    live: *const RaphaelLiveState,
    out_actions: *mut u32,
    out_capacity: i32,
) -> i32 {
    if input.is_null() || live.is_null() || out_actions.is_null() || out_capacity <= 0 {
        return ERR_INVALID_ARGS;
    }

    let input = &*input;
    let live = &*live;
    let out = std::slice::from_raw_parts_mut(out_actions, out_capacity as usize);

    write_result(std::panic::catch_unwind(move || solve_from_state(input, live)), out)
}

fn write_result(result: std::thread::Result<Result<Vec<raphael_sim::Action>, i32>>, out: &mut [u32]) -> i32 {
    match result {
        Ok(Ok(actions)) => {
            if actions.len() > out.len() {
                return ERR_BUFFER_TOO_SMALL;
            }
            for (slot, action) in out.iter_mut().zip(actions.iter()) {
                *slot = action.action_id();
            }
            actions.len() as i32
        }
        Ok(Err(code)) => code,
        Err(_) => ERR_PANIC,
    }
}

/// Writes the craft's base progress/quality (gain per 100% efficiency) for
/// the given stats and recipe. Returns 0 on success, negative on error.
///
/// # Safety
/// All pointers must be valid.
#[no_mangle]
pub unsafe extern "C" fn raphael_base_values(
    input: *const RaphaelInput,
    out_base_progress: *mut u16,
    out_base_quality: *mut u16,
) -> i32 {
    if input.is_null() || out_base_progress.is_null() || out_base_quality.is_null() {
        return ERR_INVALID_ARGS;
    }

    let input = &*input;
    match std::panic::catch_unwind(move || settings_for(input)) {
        Ok(settings) => {
            *out_base_progress = settings.base_progress;
            *out_base_quality = settings.base_quality;
            0
        }
        Err(_) => ERR_PANIC,
    }
}

fn settings_for(input: &RaphaelInput) -> raphael_sim::Settings {
    let recipe = raphael_data::Recipe {
        job_id: 0,
        item_id: 0,
        max_level_scaling: 0,
        recipe_level: input.recipe_level,
        progress_factor: 0,
        quality_factor: 0,
        durability_factor: 0,
        material_factor: 0,
        ingredients: Default::default(),
        is_expert: input.is_expert != 0,
        req_craftsmanship: 0,
        req_control: 0,
    };

    let overrides = CustomRecipeOverrides {
        max_progress_override: input.max_progress,
        max_quality_override: input.max_quality,
        max_durability_override: input.max_durability,
        ..Default::default()
    };

    let stats = CrafterStats {
        craftsmanship: input.craftsmanship,
        control: input.control,
        cp: input.cp,
        level: input.level,
        manipulation: input.manipulation != 0,
        heart_and_soul: input.heart_and_soul != 0,
        quick_innovation: input.quick_innovation != 0,
    };

    let mut settings = get_game_settings(recipe, Some(overrides), stats, None, None);
    settings.adversarial = input.adversarial != 0;
    settings.backload_progress = input.backload_progress != 0;

    if input.exclude_first_step_actions != 0 {
        settings.allowed_actions = settings
            .allowed_actions
            .remove(raphael_sim::Action::MuscleMemory)
            .remove(raphael_sim::Action::Reflect)
            .remove(raphael_sim::Action::TrainedEye);
    }

    if input.exclude_prudent != 0 {
        settings.allowed_actions = settings
            .allowed_actions
            .remove(raphael_sim::Action::PrudentSynthesis)
            .remove(raphael_sim::Action::PrudentTouch);
    }

    settings
}

fn solve(input: &RaphaelInput) -> Option<Vec<raphael_sim::Action>> {
    let mut settings = settings_for(input);

    let target_quality = input.target_quality.clamp(0, settings.max_quality);
    settings.max_quality = target_quality.saturating_sub(input.initial_quality);

    let solver_settings = SolverSettings {
        simulator_settings: settings,
        allow_non_max_quality_solutions: true,
    };

    let mut solver = MacroSolver::new(
        solver_settings,
        Box::new(|_| {}),
        Box::new(|_| {}),
        AtomicFlag::new(),
    );
    solver.solve().ok()
}

fn solve_from_state(input: &RaphaelInput, live: &RaphaelLiveState) -> Result<Vec<raphael_sim::Action>, i32> {
    let mut settings = settings_for(input);
    // The live quality already includes HQ-material initial quality, so the
    // goal is the absolute target (clamped to the recipe maximum).
    settings.max_quality = input.target_quality.clamp(0, settings.max_quality);

    let initial = Effects::initial(&settings);
    let combo = match live.combo {
        1 => Combo::SynthesisBegin,
        2 => Combo::BasicTouch,
        3 => Combo::StandardTouch,
        _ => Combo::None,
    };
    let effects = initial
        .with_inner_quiet(live.inner_quiet.min(10))
        .with_waste_not(live.waste_not.min(15))
        .with_innovation(live.innovation.min(7))
        .with_veneration(live.veneration.min(7))
        .with_great_strides(live.great_strides.min(3))
        .with_muscle_memory(live.muscle_memory.min(7))
        .with_manipulation(live.manipulation.min(15))
        .with_trained_perfection_available(
            initial.trained_perfection_available() && live.trained_perfection_available != 0,
        )
        .with_heart_and_soul_available(
            initial.heart_and_soul_available() && live.heart_and_soul_available != 0,
        )
        .with_quick_innovation_available(
            initial.quick_innovation_available() && live.quick_innovation_available != 0,
        )
        .with_trained_perfection_active(live.trained_perfection_active != 0)
        .with_heart_and_soul_active(live.heart_and_soul_active != 0)
        .with_combo(combo);

    let state = SimulationState {
        cp: live.cp.min(settings.max_cp),
        durability: live.durability.min(settings.max_durability),
        progress: live.progress,
        quality: live.quality,
        unreliable_quality: 0,
        effects,
    };

    if state.is_final(&settings) {
        return Err(ERR_STATE_IS_FINAL);
    }

    let solver_settings = SolverSettings {
        simulator_settings: settings,
        allow_non_max_quality_solutions: true,
    };

    let mut solver = MacroSolver::new(
        solver_settings,
        Box::new(|_| {}),
        Box::new(|_| {}),
        AtomicFlag::new(),
    );
    solver.solve_from_state(state).map_err(|_| ERR_SOLVER_FAILED)
}
