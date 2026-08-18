mod serial;

use serial::SerialState;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(SerialState::default())
        .invoke_handler(tauri::generate_handler![
            serial::list_serial_ports,
            serial::get_serial_status,
            serial::open_serial_port,
            serial::close_serial_port,
        ])
        .run(tauri::generate_context!())
        .expect("failed to run Nexus 2.0");
}
