use serde::{Deserialize, Serialize};
use similar::{Algorithm, ChangeTag, TextDiff};
use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use unicode_segmentation::UnicodeSegmentation;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub enum DiffAlgorithm {
    Myers,
    Patience,
    Lcs,
}

impl From<i32> for DiffAlgorithm {
    fn from(val: i32) -> Self {
        match val {
            1 => DiffAlgorithm::Patience,
            2 => DiffAlgorithm::Lcs,
            _ => DiffAlgorithm::Myers,
        }
    }
}

impl DiffAlgorithm {
    pub fn to_similar_algorithm(&self) -> Algorithm {
        match self {
            DiffAlgorithm::Myers => Algorithm::Myers,
            DiffAlgorithm::Patience => Algorithm::Patience,
            DiffAlgorithm::Lcs => Algorithm::Lcs,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub enum DiffLevel {
    Lines,
    Words,
    Chars,
    Graphemes,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DiffItem {
    pub tag: String, // "Equal", "Delete", "Insert"
    pub value: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ParagraphDiffResult {
    pub algorithm: String,
    pub level: String,
    pub items: Vec<DiffItem>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DissimilarChunk {
    pub operation: String, // "Equal", "Delete", "Insert"
    pub text: String,
}

/// `similar` 라이브러리를 사용한 diff 수행 함수
pub fn compare_texts(
    old_text: &str,
    new_text: &str,
    algo: DiffAlgorithm,
    level: DiffLevel,
) -> ParagraphDiffResult {
    let similar_algo = algo.to_similar_algorithm();

    let items = match level {
        DiffLevel::Lines => {
            let diff = TextDiff::configure()
                .algorithm(similar_algo)
                .diff_lines(old_text, new_text);

            diff.iter_all_changes()
                .map(|change| {
                    let tag = match change.tag() {
                        ChangeTag::Equal => "Equal",
                        ChangeTag::Delete => "Delete",
                        ChangeTag::Insert => "Insert",
                    };
                    DiffItem {
                        tag: tag.to_string(),
                        value: change.value().to_string(),
                    }
                })
                .collect()
        }
        DiffLevel::Words => {
            let diff = TextDiff::configure()
                .algorithm(similar_algo)
                .diff_words(old_text, new_text);

            diff.iter_all_changes()
                .map(|change| {
                    let tag = match change.tag() {
                        ChangeTag::Equal => "Equal",
                        ChangeTag::Delete => "Delete",
                        ChangeTag::Insert => "Insert",
                    };
                    DiffItem {
                        tag: tag.to_string(),
                        value: change.value().to_string(),
                    }
                })
                .collect()
        }
        DiffLevel::Chars => {
            let diff = TextDiff::configure()
                .algorithm(similar_algo)
                .diff_chars(old_text, new_text);

            diff.iter_all_changes()
                .map(|change| {
                    let tag = match change.tag() {
                        ChangeTag::Equal => "Equal",
                        ChangeTag::Delete => "Delete",
                        ChangeTag::Insert => "Insert",
                    };
                    DiffItem {
                        tag: tag.to_string(),
                        value: change.value().to_string(),
                    }
                })
                .collect()
        }
        DiffLevel::Graphemes => {
            let old_graphemes: Vec<&str> = old_text.graphemes(true).collect();
            let new_graphemes: Vec<&str> = new_text.graphemes(true).collect();

            let diff = TextDiff::configure()
                .algorithm(similar_algo)
                .diff_slices(&old_graphemes, &new_graphemes);

            diff.iter_all_changes()
                .map(|change| {
                    let tag = match change.tag() {
                        ChangeTag::Equal => "Equal",
                        ChangeTag::Delete => "Delete",
                        ChangeTag::Insert => "Insert",
                    };
                    DiffItem {
                        tag: tag.to_string(),
                        value: change.value().to_string(),
                    }
                })
                .collect()
        }
    };

    ParagraphDiffResult {
        algorithm: format!("{:?}", algo),
        level: format!("{:?}", level),
        items,
    }
}

/// `dissimilar` 라이브러리를 사용한 Myers + Semantic Cleanup diff 수행 함수
pub fn compare_dissimilar(old_text: &str, new_text: &str) -> Vec<DissimilarChunk> {
    let chunks = dissimilar::diff(old_text, new_text);
    chunks
        .into_iter()
        .map(|chunk| {
            let op = match chunk {
                dissimilar::Chunk::Equal(_) => "Equal",
                dissimilar::Chunk::Delete(_) => "Delete",
                dissimilar::Chunk::Insert(_) => "Insert",
            };
            let text = match chunk {
                dissimilar::Chunk::Equal(t) | dissimilar::Chunk::Delete(t) | dissimilar::Chunk::Insert(t) => t,
            };
            DissimilarChunk {
                operation: op.to_string(),
                text: text.to_string(),
            }
        })
        .collect()
}

// -------------------------------------------------------------------
// C-ABI Interop FFI Exports (For C# P/Invoke)
// -------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn rust_diff_compare(
    old_ptr: *const c_char,
    new_ptr: *const c_char,
    algo_code: i32,  // 0: Myers, 1: Patience, 2: Lcs
    level_code: i32, // 0: Lines, 1: Words, 2: Chars, 3: Graphemes
) -> *mut c_char {
    if old_ptr.is_null() || new_ptr.is_null() {
        return std::ptr::null_mut();
    }

    let old_str = unsafe { CStr::from_ptr(old_ptr) }.to_str().unwrap_or("");
    let new_str = unsafe { CStr::from_ptr(new_ptr) }.to_str().unwrap_or("");

    let algo = DiffAlgorithm::from(algo_code);
    let level = match level_code {
        1 => DiffLevel::Words,
        2 => DiffLevel::Chars,
        3 => DiffLevel::Graphemes,
        _ => DiffLevel::Lines,
    };

    let result = compare_texts(old_str, new_str, algo, level);
    let json = serde_json::to_string(&result).unwrap_or_default();

    CString::new(json).unwrap().into_raw()
}

#[no_mangle]
pub extern "C" fn rust_diff_dissimilar(
    old_ptr: *const c_char,
    new_ptr: *const c_char,
) -> *mut c_char {
    if old_ptr.is_null() || new_ptr.is_null() {
        return std::ptr::null_mut();
    }

    let old_str = unsafe { CStr::from_ptr(old_ptr) }.to_str().unwrap_or("");
    let new_str = unsafe { CStr::from_ptr(new_ptr) }.to_str().unwrap_or("");

    let result = compare_dissimilar(old_str, new_str);
    let json = serde_json::to_string(&result).unwrap_or_default();

    CString::new(json).unwrap().into_raw()
}

#[no_mangle]
pub extern "C" fn rust_diff_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            let _ = CString::from_raw(ptr);
        }
    }
}
