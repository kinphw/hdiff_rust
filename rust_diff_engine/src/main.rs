use rust_diff_engine::{
    compare_dissimilar, compare_texts, DiffAlgorithm, DiffLevel,
};
use std::env;
use std::fs;
use unicode_normalization::UnicodeNormalization;

fn main() {
    let args: Vec<String> = env::args().collect();

    println!("==================================================");
    println!("  Hdiff Rust Compare Engine Benchmark & Tester  ");
    println!("==================================================\n");

    let (old_text, new_text) = if args.len() >= 3 {
        let old_content = fs::read_to_string(&args[1]).expect("Failed to read file 1");
        let new_content = fs::read_to_string(&args[2]).expect("Failed to read file 2");
        (old_content, new_content)
    } else {
        println!("[Info] No input files provided. Running built-in Korean & Unicode benchmark test cases...\n");
        get_default_benchmark_cases()
    };

    println!("--- 1. Algorithm Comparison (similar crate: Myers vs Patience vs LCS) ---");
    let algos = vec![
        (DiffAlgorithm::Myers, "Myers"),
        (DiffAlgorithm::Patience, "Patience"),
        (DiffAlgorithm::Lcs, "LCS"),
    ];

    for (algo, name) in &algos {
        let res_line = compare_texts(&old_text, &new_text, algo.clone(), DiffLevel::Lines);
        let res_grapheme = compare_texts(&old_text, &new_text, algo.clone(), DiffLevel::Graphemes);

        println!(">> Algorithm: {}", name);
        println!("   [Lines Level] Total Diff Items: {}", res_line.items.len());
        print_sample_items(&res_line.items, 3);
        println!("   [Graphemes Level] Total Diff Items: {}", res_grapheme.items.len());
        println!();
    }

    println!("--- 2. Granularity Test (Char vs Grapheme - Korean NFD/NFC & Emoji) ---");
    let char_diff = compare_texts(&old_text, &new_text, DiffAlgorithm::Patience, DiffLevel::Chars);
    let grapheme_diff = compare_texts(&old_text, &new_text, DiffAlgorithm::Patience, DiffLevel::Graphemes);
    println!("   Chars Level Diff Count: {}", char_diff.items.len());
    println!("   Graphemes Level Diff Count: {}", grapheme_diff.items.len());
    println!();

    println!("--- 3. Semantic Cleanup (dissimilar crate: DMP + Cleanup) ---");
    let dissimilar_res = compare_dissimilar(&old_text, &new_text);
    println!("   Dissimilar Chunk Count: {}", dissimilar_res.len());
    print!("   Formatted Result: ");
    for chunk in &dissimilar_res {
        match chunk.operation.as_str() {
            "Equal" => print!("{}", chunk.text),
            "Delete" => print!("[-{}-]", chunk.text),
            "Insert" => print!("[+{}+]", chunk.text),
            _ => {}
        }
    }
    println!("\n");

    println!("==================================================");
    println!("  Rust Engine Benchmark Completed Successfully! ");
    println!("==================================================");
}

fn print_sample_items(items: &[rust_diff_engine::DiffItem], limit: usize) {
    for item in items.iter().take(limit) {
        let tag_mark = match item.tag.as_str() {
            "Equal" => " ",
            "Delete" => "-",
            "Insert" => "+",
            _ => "?",
        };
        let preview = item.value.trim();
        let truncated = if preview.chars().count() > 40 {
            format!("{}...", preview.chars().take(40).collect::<String>())
        } else {
            preview.to_string()
        };
        println!("     [{}] {}", tag_mark, truncated);
    }
}

fn get_default_benchmark_cases() -> (String, String) {
    let old_doc = r#"제1조 (목적)
본 약관은 회사가 제공하는 서비스의 이용조건 및 절차에 관한 사항을 규정함을 목적으로 합니다.

제2조 (용어의 정의)
1. "회원"이라 함은 회사의 서비스에 접속하여 본 약관에 따라 서비스를 이용하는 고객을 말합니다.
2. "콘텐츠"라 함은 서비스 상에 게시된 글, 사진, 동영상 등 제반 정보를 의미합니다.

제3조 (약관의 효력)
약관은 공지함으로 효력이 발생합니다. 👨‍👩‍👧‍👦 가족 서비스 추가 예정.
한글 자모 테스트: 한글(NFD 분해형)"#;

    let new_doc = r#"제1조 (목적)
본 약관은 회사가 제공하는 전자상거래 서비스의 이용조건, 절차 및 권리·의무를 규정함을 목적으로 합니다.

제2조 (용어의 정의)
1. "회원"이라 함은 회사의 서비스에 접속하여 본 약관에 따라 회사와 이용계약을 체결하고 서비스를 이용하는 고객을 말합니다.
2. "콘텐츠"라 함은 서비스 상에 게시된 글, 사진, 이미지, 동영상 등 모든 디지털 정보를 의미합니다.

제3조 (약관의 효력 및 변경)
약관은 서비스 내에 공지함으로써 효력이 발생합니다. 👨‍👩‍👧‍👦 가족 멤버십 서비스 신설.
한글 자모 테스트: 한글(NFC 완성형)"#;

    (old_doc.nfd().collect::<String>(), new_doc.nfc().collect::<String>())
}
