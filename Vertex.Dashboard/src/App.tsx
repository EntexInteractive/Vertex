import {Route, Routes} from "react-router-dom";
import {Dashboard} from "./pages/dashboard.tsx";
import './App.css'
import {Sidebar} from "./components/sidebar/sidebar.tsx";

export default function App() {
    return (
        <div className="app-shell">
            <Sidebar />

            <main className="app-content">
                <Routes>
                    <Route path="/" element={<Dashboard />} />
                </Routes>
            </main>
        </div>
    );
}
