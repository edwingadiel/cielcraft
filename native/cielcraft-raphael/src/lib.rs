//! C ABI wrapper around raphael-solver (spec §13/§14).
//!
//! The custom-recipe path is used so the plugin supplies live recipe values
//! (rlvl, max progress/quality/durability) read from the game, and no recipe
//! database is consulted here.

use raphael_data::{get_game_settings, CrafterStats, CustomRecipeOverrides};
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
}

pub const ERR_INVALID_ARGS: i32 = -1;
pub const ERR_SOLVER_FAILED: i32 = -2;
pub const ERR_PANIC: i32 = -3;
pub const ERR_BUFFER_TOO_SMALL: i32 = -4;

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

    match std::panic::catch_unwind(move || solve(input)) {
        Ok(Some(actions)) => {
            if actions.len() > out.len() {
                return ERR_BUFFER_TOO_SMALL;
            }
            for (slot, action) in out.iter_mut().zip(actions.iter()) {
                *slot = action.action_id();
            }
            actions.len() as i32
        }
        Ok(None) => ERR_SOLVER_FAILED,
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
