use anyhow::{Context, Result};
use serde_json::{json, Map, Value};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

pub fn state_path(codex_home: &Path) -> PathBuf {
    codex_home.join(".codex-global-state.json")
}

/// What `register_projects` changed inside the desktop state file.
#[derive(Debug, Default)]
pub struct RegistrationOutcome {
    /// True when the target uses the Codex 26.x project model (local-projects).
    pub project_model: bool,
    pub created_projects: usize,
    pub assigned_threads: usize,
    /// Imported project path -> desktop project id, for state DB linking.
    pub project_id_by_path: BTreeMap<String, String>,
}

pub fn register_projects(
    path: &Path,
    projects: &BTreeSet<String>,
    thread_paths: &BTreeMap<String, String>,
) -> Result<RegistrationOutcome> {
    if projects.is_empty() && thread_paths.is_empty() {
        return Ok(RegistrationOutcome::default());
    }
    let mut root = if path.is_file() {
        serde_json::from_slice::<Value>(&fs::read(path)?)
            .with_context(|| format!("parse {}", path.display()))?
    } else {
        Value::Object(Map::new())
    };
    if !root.is_object() {
        root = Value::Object(Map::new());
    }
    let root_object = root.as_object_mut().expect("root was converted to object");

    let outcome = if uses_project_model(root_object) {
        register_modern_projects(root_object, projects, thread_paths)
    } else {
        // Pre-project Codex releases only know about workspace root paths.
        register_legacy_project_arrays(root_object, projects);
        let state = ensure_object(root_object, "electron-persisted-atom-state");
        register_legacy_project_arrays(state, projects);
        RegistrationOutcome::default()
    };

    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temporary = path.with_extension("json.tmp");
    fs::write(&temporary, serde_json::to_vec(&root)?)?;
    fs::rename(&temporary, path)?;
    Ok(outcome)
}

fn uses_project_model(root: &Map<String, Value>) -> bool {
    root.contains_key("local-projects")
        || root.contains_key("app-server-projects-migration-by-host")
        || root.contains_key("app-server-project-id-by-legacy-project-id-by-host")
}

fn register_modern_projects(
    root: &mut Map<String, Value>,
    projects: &BTreeSet<String>,
    thread_paths: &BTreeMap<String, String>,
) -> RegistrationOutcome {
    let now = now_millis();
    let mut outcome = RegistrationOutcome {
        project_model: true,
        ..RegistrationOutcome::default()
    };

    let local = ensure_object(root, "local-projects");
    let mut project_id_by_root: BTreeMap<String, String> = BTreeMap::new();
    for (id, value) in local.iter() {
        if let Some(paths) = value.get("rootPaths").and_then(Value::as_array) {
            for item in paths {
                if let Some(text) = item.as_str() {
                    project_id_by_root
                        .entry(normalize_path(text))
                        .or_insert_with(|| id.clone());
                }
            }
        }
    }

    let mut created_ids = Vec::new();
    for project in projects {
        if project.trim().is_empty() {
            continue;
        }
        let key = normalize_path(project);
        if let Some(id) = project_id_by_root.get(&key) {
            outcome.project_id_by_path.insert(project.clone(), id.clone());
            continue;
        }
        let id = Uuid::new_v4().to_string();
        local.insert(
            id.clone(),
            json!({
                "id": id,
                "name": project_name(project),
                "rootPaths": [project],
                "createdAt": now,
                "updatedAt": now,
            }),
        );
        project_id_by_root.insert(key, id.clone());
        outcome.project_id_by_path.insert(project.clone(), id.clone());
        outcome.created_projects += 1;
        created_ids.push(id);
    }

    // Codex 26.x stores project ids in project-order; older builds wrote plain
    // paths there. Drop those leftovers so every entry resolves again, then
    // append the projects created above.
    let order = ensure_array(root, "project-order");
    order.retain(|value| value.as_str().map(|text| !looks_like_path(text)).unwrap_or(true));
    for id in &created_ids {
        if !order.iter().any(|value| value.as_str() == Some(id.as_str())) {
            order.push(Value::String(id.clone()));
        }
    }

    if !thread_paths.is_empty() {
        assign_threads(root, thread_paths, &project_id_by_root, &mut outcome);
    }

    // Keep the legacy workspace-root arrays in sync so the folder picker and
    // older builds still see the imported projects.
    append_paths(root, &["electron-saved-workspace-roots"], projects);
    let state = ensure_object(root, "electron-persisted-atom-state");
    append_paths(
        state,
        &["electron-saved-workspace-roots", "project-order"],
        projects,
    );

    outcome
}

fn assign_threads(
    root: &mut Map<String, Value>,
    thread_paths: &BTreeMap<String, String>,
    project_id_by_root: &BTreeMap<String, String>,
    outcome: &mut RegistrationOutcome,
) {
    let mut assigned = BTreeMap::new();
    {
        let assignments = ensure_object(root, "thread-project-assignments");
        for (thread_id, project_path) in thread_paths {
            let Some(project_id) = project_id_by_root.get(&normalize_path(project_path)) else {
                continue;
            };
            assignments
                .entry(thread_id.clone())
                .or_insert_with(|| json!({"projectKind": "local", "projectId": project_id}));
            assigned.insert(thread_id.clone(), project_id.clone());
        }
    }
    if assigned.is_empty() {
        return;
    }

    {
        let orders = ensure_object(root, "sidebar-project-thread-orders");
        for (thread_id, project_id) in &assigned {
            let group = orders
                .entry(project_id.clone())
                .or_insert_with(|| json!({"threadIds": []}));
            if !group.is_object() {
                *group = json!({"threadIds": []});
            }
            let group = group.as_object_mut().expect("group was converted to object");
            let list = ensure_array(group, "threadIds");
            if !list
                .iter()
                .any(|value| value.as_str() == Some(thread_id.as_str()))
            {
                list.insert(0, Value::String(thread_id.clone()));
            }
        }
    }

    if let Some(list) = root
        .get_mut("projectless-thread-ids")
        .and_then(Value::as_array_mut)
    {
        list.retain(|value| {
            value
                .as_str()
                .map(|thread_id| !assigned.contains_key(thread_id))
                .unwrap_or(true)
        });
    }
    outcome.assigned_threads = assigned.len();
}

fn register_legacy_project_arrays(object: &mut Map<String, Value>, projects: &BTreeSet<String>) {
    append_paths(
        object,
        &["electron-saved-workspace-roots", "project-order"],
        projects,
    );
}

fn append_paths(object: &mut Map<String, Value>, keys: &[&str], projects: &BTreeSet<String>) {
    for key in keys {
        let values = ensure_array(object, key);
        for project in projects {
            if !values
                .iter()
                .any(|value| value.as_str() == Some(project.as_str()))
            {
                values.push(Value::String(project.clone()));
            }
        }
    }
}

fn ensure_object<'a>(object: &'a mut Map<String, Value>, key: &str) -> &'a mut Map<String, Value> {
    let value = object
        .entry(key)
        .or_insert_with(|| Value::Object(Map::new()));
    if !value.is_object() {
        *value = Value::Object(Map::new());
    }
    value.as_object_mut().expect("value was converted to object")
}

fn ensure_array<'a>(object: &'a mut Map<String, Value>, key: &str) -> &'a mut Vec<Value> {
    let value = object
        .entry(key)
        .or_insert_with(|| Value::Array(Vec::new()));
    if !value.is_array() {
        *value = Value::Array(Vec::new());
    }
    value.as_array_mut().expect("value was converted to array")
}

fn normalize_path(path: &str) -> String {
    let mut value = path.trim().to_owned();
    for prefix in ["\\\\?\\UNC\\", "\\\\?\\"] {
        if value.starts_with(prefix) {
            value = value[prefix.len()..].to_owned();
            break;
        }
    }
    value = value.replace('/', "\\");
    while value.len() > 3 && value.ends_with('\\') {
        value.pop();
    }
    value.to_ascii_lowercase()
}

fn looks_like_path(value: &str) -> bool {
    let text = value.trim();
    text.contains('\\')
        || text.contains('/')
        || (text.len() >= 2 && text.as_bytes()[1] == b':')
}

fn project_name(path: &str) -> String {
    let trimmed = path.trim().trim_end_matches(['\\', '/']);
    trimmed
        .rsplit(['\\', '/'])
        .next()
        .filter(|name| !name.is_empty())
        .unwrap_or(trimmed)
        .to_owned()
}

fn now_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as i64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn thread_paths(entries: &[(&str, &str)]) -> BTreeMap<String, String> {
        entries
            .iter()
            .map(|(thread, path)| (thread.to_string(), path.to_string()))
            .collect()
    }

    #[test]
    fn adds_projects_without_removing_existing_state() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join(".codex-global-state.json");
        fs::write(
            &path,
            br#"{"electron-persisted-atom-state":{"other":true,"project-order":["/existing"]}}"#,
        )
        .unwrap();
        register_projects(
            &path,
            &["/new".to_owned(), "/existing".to_owned()]
                .into_iter()
                .collect(),
            &thread_paths(&[]),
        )
        .unwrap();
        let value: Value = serde_json::from_slice(&fs::read(path).unwrap()).unwrap();
        assert_eq!(value["project-order"].as_array().unwrap().len(), 2);
        assert_eq!(
            value["electron-saved-workspace-roots"]
                .as_array()
                .unwrap()
                .len(),
            2
        );
        let state = &value["electron-persisted-atom-state"];
        assert_eq!(state["other"], true);
        assert_eq!(state["project-order"].as_array().unwrap().len(), 2);
        assert_eq!(
            state["electron-saved-workspace-roots"]
                .as_array()
                .unwrap()
                .len(),
            2
        );
    }

    #[test]
    fn registers_imported_projects_in_modern_model() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join(".codex-global-state.json");
        fs::write(
            &path,
            br#"{
                "local-projects": {
                    "11111111-1111-4111-8111-111111111111": {
                        "id": "11111111-1111-4111-8111-111111111111",
                        "name": "Existing",
                        "rootPaths": ["C:\\Work\\Existing"],
                        "createdAt": 1,
                        "updatedAt": 1
                    }
                },
                "project-order": [
                    "11111111-1111-4111-8111-111111111111",
                    "C:\\Work\\Legacy"
                ],
                "thread-project-assignments": {},
                "projectless-thread-ids": ["thread-a"],
                "electron-saved-workspace-roots": ["C:/Work/Existing"]
            }"#,
        )
        .unwrap();
        let projects = ["C:\\Work\\Existing".to_owned(), "c:/work/new-project".to_owned()]
            .into_iter()
            .collect();
        let outcome = register_projects(
            &path,
            &projects,
            &thread_paths(&[("thread-a", "C:\\Work\\New-Project")]),
        )
        .unwrap();

        assert!(outcome.project_model);
        assert_eq!(outcome.created_projects, 1);
        assert_eq!(outcome.assigned_threads, 1);
        assert_eq!(
            outcome
                .project_id_by_path
                .get("C:\\Work\\Existing")
                .map(String::as_str),
            Some("11111111-1111-4111-8111-111111111111")
        );

        let value: Value = serde_json::from_slice(&fs::read(path).unwrap()).unwrap();
        let local = value["local-projects"].as_object().unwrap();
        assert_eq!(local.len(), 2, "existing project must be reused");
        let new_id = outcome
            .project_id_by_path
            .get("c:/work/new-project")
            .unwrap()
            .clone();
        assert_eq!(local[&new_id]["name"], "new-project");
        assert_eq!(local[&new_id]["rootPaths"][0], "c:/work/new-project");

        let order = value["project-order"].as_array().unwrap();
        assert!(order
            .iter()
            .any(|entry| entry.as_str() == Some(new_id.as_str())));
        assert!(order
            .iter()
            .all(|entry| !entry.as_str().unwrap().contains('\\')), "project ids only");

        assert_eq!(
            value["thread-project-assignments"]["thread-a"]["projectId"],
            new_id
        );
        assert!(value["projectless-thread-ids"].as_array().unwrap().is_empty());
        assert_eq!(
            value["sidebar-project-thread-orders"][&new_id]["threadIds"][0],
            "thread-a"
        );

        let roots = value["electron-saved-workspace-roots"].as_array().unwrap();
        assert!(roots
            .iter()
            .any(|entry| entry.as_str() == Some("c:/work/new-project")));
        let state = &value["electron-persisted-atom-state"];
        assert!(state["project-order"]
            .as_array()
            .unwrap()
            .iter()
            .any(|entry| entry.as_str() == Some("c:/work/new-project")));
    }

    #[test]
    fn matches_existing_projects_across_separators_and_case() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join(".codex-global-state.json");
        fs::write(
            &path,
            br#"{"local-projects":{"22222222-2222-4222-8222-222222222222":{
                "id":"22222222-2222-4222-8222-222222222222",
                "name":"Proj",
                "rootPaths":["\\\\?\\C:\\Work\\Proj"],
                "createdAt":1,"updatedAt":1}},"project-order":[]}"#,
        )
        .unwrap();
        let projects = ["c:/work/proj".to_owned()].into_iter().collect();
        let outcome = register_projects(&path, &projects, &thread_paths(&[])).unwrap();
        assert_eq!(outcome.created_projects, 0);
        let value: Value = serde_json::from_slice(&fs::read(path).unwrap()).unwrap();
        assert_eq!(value["local-projects"].as_object().unwrap().len(), 1);
    }
}
