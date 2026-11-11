use std::{env, fs, path::PathBuf};

fn main() {
    let mut output = String::new();
    let mut common = std::collections::BTreeMap::new();
    let mut profiles = Vec::new();
    for version in ["6.13.1", "6.13.3"] {
        let path = format!("../../compatibility/sampled-timing-{version}.profile");
        println!("cargo:rerun-if-changed={path}");
        let bytes = fs::read(path).expect("timing compatibility profile");
        let fingerprint = bytes.iter().fold(14695981039346656037u64,
            |hash, value| (hash ^ u64::from(*value)).wrapping_mul(1099511628211));
        let mut offsets = std::collections::BTreeMap::new();
        for line in std::str::from_utf8(&bytes).unwrap().lines() {
            if let Some((name, value)) = line.split_once('=') {
                if let Some(hex) = value.strip_prefix("0x") {
                    let value = usize::from_str_radix(hex, 16).expect("profile offset");
                    assert!(name.chars().all(|c| c.is_ascii_alphanumeric()));
                    assert!(offsets.insert(name.to_owned(), value).is_none());
                }
            }
        }
        if profiles.is_empty() {
            common = offsets.clone();
            output.push_str(&format!("pub const FINGERPRINT: u64 = {fingerprint};\n"));
            let addresses: Vec<_> = offsets.iter().filter(|(name, _)|
                name.starts_with("Dse") || name.starts_with("Vsm") || name.starts_with("DurationCaller")).collect();
            output.push_str(&format!("pub const ADDRESS_COUNT: usize = {};\n", addresses.len()));
            output.push_str(&format!("pub const ADDRESS_IS_VSM: &[bool] = &[{}];\n",
                addresses.iter().map(|(name, _)| name.starts_with("Vsm").to_string()).collect::<Vec<_>>().join(",")));
            output.push_str(&format!("#[cfg(test)] pub const DEFAULT_ADDRESSES: &[u32] = &[{}];\n",
                addresses.iter().map(|(_, value)| value.to_string()).collect::<Vec<_>>().join(",")));
            for (index, (name, value)) in addresses.iter().enumerate() {
                output.push_str(&format!("pub fn {name}() -> usize {{ address({index}, {value}) }}\n"));
            }
            for (name, value) in &offsets {
                if !addresses.iter().any(|(key, _)| *key == name) {
                    output.push_str(&format!("pub const {name}: usize = {value};\n"));
                }
            }
        } else {
            // These two certified builds share DSE code and private structures.
            // A third layout must introduce an adapter rather than silently use
            // the first profile's constants for different fields.
            assert_eq!(offsets.len(), common.len());
            for (name, value) in &offsets {
                if !["VsmEncodeNote", "VsmEncodeSinger", "VsmMapScore", "VsmPrepare", "VsmCacheHash"].contains(&name.as_str()) {
                    assert_eq!(common.get(name), Some(value), "new native layout needs an adapter: {name}");
                }
            }
        }
        profiles.push(format!("({fingerprint}, {}, {})", offsets["VsmPrepare"], offsets["VsmCacheHash"]));
    }
    output.push_str(&format!("pub const PROFILES: &[(u64, usize, usize)] = &[{}];\n", profiles.join(",")));
    fs::write(PathBuf::from(env::var_os("OUT_DIR").unwrap()).join("timing_profile.rs"), output).unwrap();
}
