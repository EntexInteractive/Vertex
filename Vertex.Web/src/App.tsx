import {useState} from 'react'
import './App.css'
import {Sidebar} from "./components/Sidebar.tsx";

export default function App() {
    const [currentPage, setCurrentPage] = useState("home");

    return (
        <div className="desktop-app">
            <div className="app-body">
                <Sidebar currentPage={currentPage} onPageChange={setCurrentPage} />
            </div>
        </div>
    )
}
